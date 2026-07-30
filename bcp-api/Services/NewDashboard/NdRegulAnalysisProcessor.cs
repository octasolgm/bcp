using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.Entities;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Infrastructure.NewDashboard;
using Reguliq.Api.Models;
using Reguliq.Api.Services.LandingAi;
using Reguliq.Api.Services.Llm;
using Reguliq.Api.Services.Storage;

namespace Reguliq.Api.Services.NewDashboard;

/// <summary>
/// Regul.ai-style pipeline: forward judgment → reverse coverage → optional qualitative.
/// Internal section extraction uses Landing AI; forward/reverse/qualitative use admin LLM.
/// Forward results sync to analysis_points so gap UI + Excel/PDF export work like V8.
/// </summary>
public class NdRegulAnalysisProcessor(
    AppDbContext db,
    RegulWorkflowLlmSettingsService llmSettings,
    RegulWorkflowLlmService regulLlm,
    NdInternalParseService internalParse,
    LandingAiPolicyClauseExtractService policyClauseExtract,
    SupabaseStorageService storage,
    NdAnalysisRunCancellationTracker runCancellation,
    ILogger<NdRegulAnalysisProcessor> logger)
{
    private const string JudgmentJsonInstruction =
        "Respond with ONLY a JSON object (no markdown fences) with keys: " +
        "design_status, operating_status, overall_status, confidence, interpretation, " +
        "policy_extract (array of strings), document_reference, gap_description, suggested_action, gap_direction.";

    private const string ReverseMappingJsonInstruction =
        "Respond with ONLY a JSON object (no markdown fences) with keys: " +
        "mapped_clause_nos (array of strings), mapping (covered|no_regulatory_basis|basis_not_verifiable), " +
        "commentary, confidence (0-1), contradicts_regulation (boolean).";

    private const string QualitativeJsonInstruction =
        "Respond with ONLY a JSON object (no markdown fences) with keys: " +
        "overall_rating (strong|adequate|weak), dimensions (array of 5 objects with dimension, rating, commentary, examples), " +
        "strengths (array of strings), improvement_recommendations (array of strings). " +
        "dimensions must include exactly one entry for each of: clarity_and_tone, structure_and_navigation, " +
        "depth_of_implementation_detail, alignment_with_regulatory_language, actionability_for_staff.";

    public async Task ProcessRunAsync(Guid runId, CancellationToken ct)
    {
        var run = await db.NdAnalysisRuns
            .Include(r => r.Points)
            .FirstOrDefaultAsync(r => r.Id == runId, ct)
            ?? throw new InvalidOperationException("Analysis run not found.");

        if (!AnalysisWorkflowEngine.IsRegulPipeline(run.WorkflowEngine))
            throw new InvalidOperationException("Run is not a Regul workflow analysis.");

        if (run.RegulClausesConfirmedAt == null)
            throw new InvalidOperationException(
                "Regul clauses must be confirmed before analysis. Call POST /nd/analysis-runs/{id}/confirm-clauses first.");

        if (runCancellation.IsStopRequested(runId))
        {
            await MarkCancelledAsync(run, ct);
            return;
        }

        var llm = await llmSettings.GetConfigAsync(ct);
        run.RegulLlmProvider = llm.Provider;
        run.RegulLlmModel = llm.Model;
        run.Status = "running";
        run.RegulPipelinePhase = "forward";
        run.RegulPipelineError = null;
        run.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        try
        {
            await EnsureForwardFindingsAsync(run, ct);
            await RunForwardPhaseAsync(run, ct);
            if (runCancellation.IsStopRequested(runId))
            {
                await MarkCancelledAsync(run, ct);
                return;
            }

            run.RegulPipelinePhase = "reverse";
            run.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await RunReversePhaseAsync(run, ct);

            if (run.EnableQualitative)
            {
                if (runCancellation.IsStopRequested(runId))
                {
                    await MarkCancelledAsync(run, ct);
                    return;
                }

                run.RegulPipelinePhase = "qualitative";
                run.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                await RunQualitativePhaseAsync(run, ct);
            }

            run.RegulPipelinePhase = "done";
            run.Status = "completed";
            await FinalizePointCountsAsync(run, ct);
            run.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException)
        {
            await MarkCancelledAsync(run, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Regul pipeline failed for run {RunId}", runId);
            run.Status = "failed";
            run.RegulPipelineError = ex.Message;
            run.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    private async Task EnsureForwardFindingsAsync(NdAnalysisRun run, CancellationToken ct)
    {
        var existing = await db.NdRegulForwardFindings
            .Where(f => f.AnalysisRunId == run.Id)
            .Select(f => f.AnalysisPointId)
            .ToListAsync(ct);

        var existingSet = existing.Where(id => id.HasValue).Select(id => id!.Value).ToHashSet();
        foreach (var point in run.Points)
        {
            if (existingSet.Contains(point.Id)) continue;

            var (clauseNo, clauseText) = ParseClauseFromSnapshot(point.PointSnapshot);
            db.NdRegulForwardFindings.Add(new NdRegulForwardFinding
            {
                AnalysisRunId = run.Id,
                AnalysisPointId = point.Id,
                ClauseNo = clauseNo,
                ClauseText = clauseText,
                Status = "pending",
            });
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task RunForwardPhaseAsync(NdAnalysisRun run, CancellationToken ct)
    {
        var policyContext = await BuildPolicyContextAsync(run, ct);
        var pending = await db.NdRegulForwardFindings
            .Where(f => f.AnalysisRunId == run.Id && f.Status == "pending" && f.AnalysisPointId != null)
            .ToListAsync(ct);

        var pointById = run.Points.ToDictionary(p => p.Id);
        var completed = 0;

        foreach (var finding in pending)
        {
            if (runCancellation.IsStopRequested(run.Id)) throw new OperationCanceledException();
            if (!finding.AnalysisPointId.HasValue || !pointById.TryGetValue(finding.AnalysisPointId.Value, out var point))
                continue;

            try
            {
                var judgment = await CallForwardJudgmentAsync(finding.ClauseNo, finding.ClauseText, policyContext, ct);
                var landingMessage = NdRegulJudgmentFormatter.FormatLandingMessage(
                    finding.ClauseNo, finding.ClauseText, judgment);

                finding.Status = "completed";
                finding.ResultJson = JsonSerializer.Serialize(judgment);
                finding.ErrorMessage = null;
                finding.UpdatedAt = DateTimeOffset.UtcNow;

                NdRegulAnalysisPointSync.ApplyForwardJudgment(point, judgment, landingMessage);
                completed++;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Regul forward judgment failed for clause {ClauseNo}", finding.ClauseNo);
                finding.Status = "failed";
                finding.ErrorMessage = ex.Message;
                finding.UpdatedAt = DateTimeOffset.UtcNow;
                point.LandingAiStatus = "failed";
                point.LandingAiError = ex.Message;
                point.UpdatedAt = DateTimeOffset.UtcNow;
            }

            run.ProcessedPointsCount = completed;
            run.LandingAiCompletedCount = completed;
            run.DualVerifyCompletedCount = completed;
            run.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        logger.LogInformation(
            "Regul forward phase completed for run {RunId} ({Completed}/{Total})",
            run.Id,
            completed,
            pending.Count);
    }

    private async Task<RegulJudgmentResult> CallForwardJudgmentAsync(
        string clauseNo,
        string clauseText,
        string policyContext,
        CancellationToken ct)
    {
        var prompt = string.Join("\n\n", new[]
        {
            NdRegulPromptDefaults.JudgmentSystemPrompt.Trim(),
            NdRegulPromptDefaults.BuildJudgmentContextText(policyContext),
            NdRegulPromptDefaults.BuildJudgmentQueryText(clauseNo, clauseText),
            JudgmentJsonInstruction,
        });

        var raw = await regulLlm.AnalyzeTextAsync(prompt, ct);
        return NdRegulLlmJsonHelper.ParseJsonObject<RegulJudgmentResult>(raw);
    }

    private async Task<string> BuildPolicyContextAsync(NdAnalysisRun run, CancellationToken ct)
    {
        var internalDocIds = JsonSerializer.Deserialize<List<string>>(run.SelectedInternalDocIds) ?? [];
        if (internalDocIds.Count == 0)
            return "No internal policy text was attached to this run.";

        var payloads = await LoadInternalDocPayloadsAsync(internalDocIds, ct);
        if (payloads.Count == 0)
            return "No internal policy text could be loaded.";

        if (payloads.Count == 1)
            return payloads[0].Markdown;

        return string.Join(
            "\n\n",
            payloads.Select((d, i) => $"=== DOCUMENT: {d.FileName} ===\n{d.Markdown}"));
    }

    private async Task<List<InternalDocPayload>> LoadInternalDocPayloadsAsync(
        List<string> internalDocIds,
        CancellationToken ct)
    {
        var result = new List<InternalDocPayload>();
        foreach (var idStr in internalDocIds)
        {
            if (!Guid.TryParse(idStr, out var docId)) continue;
            var doc = await db.StoredDocuments.FirstOrDefaultAsync(d => d.Id == docId, ct);
            if (doc == null || string.IsNullOrWhiteSpace(doc.StoragePath)) continue;
            if (!storage.IsConfigured) continue;
            var bytes = await storage.DownloadAsync(doc.StoragePath, ct);
            result.Add(await internalParse.EnsureParsedAsync(doc, bytes, ct));
        }
        return result;
    }

    private async Task RunReversePhaseAsync(NdAnalysisRun run, CancellationToken ct)
    {
        await ExtractAndStoreInternalSectionsAsync(run, ct);
        await ClearIntReverseArtifactsAsync(run.Id, ct);

        var sections = await db.NdRegulInternalSections
            .Where(s => s.AnalysisRunId == run.Id)
            .OrderBy(s => s.SectionRef)
            .ToListAsync(ct);

        var forwardFindings = await db.NdRegulForwardFindings
            .Where(f => f.AnalysisRunId == run.Id && f.AnalysisPointId != null)
            .OrderBy(f => f.ClauseNo)
            .ToListAsync(ct);

        if (forwardFindings.Count == 0)
            throw new InvalidOperationException("No forward regulatory clauses available for reverse mapping.");

        var regulatoryClauses = forwardFindings
            .Select(f => (f.ClauseNo, f.ClauseText))
            .ToList();
        var regulatoryByNo = forwardFindings
            .GroupBy(f => f.ClauseNo)
            .ToDictionary(g => g.Key, g => g.First().ClauseText);

        var intRowsCreated = 0;
        var mappingsCompleted = 0;

        foreach (var section in sections)
        {
            if (runCancellation.IsStopRequested(run.Id)) throw new OperationCanceledException();

            var reverseRow = new NdRegulReverseMapping
            {
                AnalysisRunId = run.Id,
                InternalSectionId = section.Id,
                Status = "pending",
            };
            db.NdRegulReverseMappings.Add(reverseRow);
            await db.SaveChangesAsync(ct);

            try
            {
                var mapping = await CallReverseMappingAsync(section, regulatoryClauses, ct);
                reverseRow.Status = "completed";
                reverseRow.Mapping = mapping.Mapping;
                reverseRow.MappedClauseNos = JsonSerializer.Serialize(mapping.MappedClauseNos);
                reverseRow.ResultJson = JsonSerializer.Serialize(mapping);
                reverseRow.ErrorMessage = null;
                reverseRow.UpdatedAt = DateTimeOffset.UtcNow;
                mappingsCompleted++;

                if (NdRegulReverseIntRows.ShouldCreateIntRow(mapping.Mapping))
                {
                    var intFinding = NdRegulReverseIntRows.BuildIntFinding(
                        run.Id,
                        section.SectionRef,
                        section.SectionText,
                        section.SourceDoc,
                        section.SourcePage,
                        mapping.Mapping,
                        mapping.MappedClauseNos,
                        regulatoryByNo,
                        mapping.ContradictsRegulation,
                        mapping.Commentary,
                        mapping.Confidence);

                    var intPoint = new NdAnalysisPoint
                    {
                        AnalysisRunId = run.Id,
                        RegulationPointId = null,
                        PointSnapshot = JsonSerializer.Serialize(new
                        {
                            pointNumber = intFinding.ClauseNo,
                            pointTitle = $"Internal section {section.SectionRef}",
                            pointContent = section.SectionText,
                        }),
                    };
                    db.NdAnalysisPoints.Add(intPoint);
                    await db.SaveChangesAsync(ct);

                    intFinding.AnalysisPointId = intPoint.Id;
                    db.NdRegulForwardFindings.Add(intFinding);

                    var judgment = NdRegulReverseIntRows.ToJudgmentResult(
                        mapping.Mapping,
                        mapping.ContradictsRegulation,
                        mapping.Confidence,
                        mapping.Commentary,
                        section.SectionText,
                        section.SourceDoc);
                    var landingMessage = NdRegulJudgmentFormatter.FormatLandingMessage(
                        intFinding.ClauseNo, intFinding.ClauseText, judgment);
                    NdRegulAnalysisPointSync.ApplyIntReverseFinding(intPoint, landingMessage, judgment);
                    intRowsCreated++;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Regul reverse mapping failed for section {SectionRef}", section.SectionRef);
                reverseRow.Status = "failed";
                reverseRow.ErrorMessage = ex.Message;
                reverseRow.UpdatedAt = DateTimeOffset.UtcNow;
            }

            await db.SaveChangesAsync(ct);
        }

        logger.LogInformation(
            "Regul reverse phase completed for run {RunId} ({Mappings}/{Sections} mapped, {IntRows} INT rows)",
            run.Id,
            mappingsCompleted,
            sections.Count,
            intRowsCreated);
    }

    private async Task<RegulReverseMappingResult> CallReverseMappingAsync(
        NdRegulInternalSection section,
        IReadOnlyList<(string ClauseNo, string ClauseText)> regulatoryClauses,
        CancellationToken ct)
    {
        var sourceDoc = section.SourceDoc ?? "internal policy";
        var prompt = string.Join("\n\n", new[]
        {
            NdRegulPromptDefaults.ReverseMappingSystemPrompt.Trim(),
            NdRegulPromptDefaults.BuildReverseMappingContextText(regulatoryClauses),
            NdRegulPromptDefaults.BuildReverseMappingQueryText(section.SectionRef, sourceDoc, section.SectionText),
            ReverseMappingJsonInstruction,
        });

        var raw = await regulLlm.AnalyzeTextAsync(prompt, ct);
        return NdRegulLlmJsonHelper.ParseJsonObject<RegulReverseMappingResult>(raw);
    }

    private async Task ClearIntReverseArtifactsAsync(Guid runId, CancellationToken ct)
    {
        var intFindings = await db.NdRegulForwardFindings
            .Where(f => f.AnalysisRunId == runId && f.ClauseNo.StartsWith(NdRegulReverseIntRows.IntClausePrefix))
            .ToListAsync(ct);
        if (intFindings.Count > 0)
            db.NdRegulForwardFindings.RemoveRange(intFindings);

        var reverseMappings = await db.NdRegulReverseMappings
            .Where(m => m.AnalysisRunId == runId)
            .ToListAsync(ct);
        if (reverseMappings.Count > 0)
            db.NdRegulReverseMappings.RemoveRange(reverseMappings);

        var intPoints = await db.NdAnalysisPoints
            .Where(p => p.AnalysisRunId == runId && p.RegulationPointId == null)
            .ToListAsync(ct);
        if (intPoints.Count > 0)
            db.NdAnalysisPoints.RemoveRange(intPoints);

        await db.SaveChangesAsync(ct);
    }

    private async Task ExtractAndStoreInternalSectionsAsync(NdAnalysisRun run, CancellationToken ct)
    {
        var internalDocIds = JsonSerializer.Deserialize<List<string>>(run.SelectedInternalDocIds) ?? [];
        if (internalDocIds.Count == 0)
            throw new InvalidOperationException("No internal documents selected for this Regul workflow run.");

        var existing = await db.NdRegulInternalSections
            .Where(s => s.AnalysisRunId == run.Id)
            .ToListAsync(ct);
        if (existing.Count > 0)
            db.NdRegulInternalSections.RemoveRange(existing);

        var allSections = new List<NdRegulInternalSection>();

        foreach (var idStr in internalDocIds)
        {
            if (!Guid.TryParse(idStr, out var docId)) continue;

            var doc = await db.StoredDocuments.FirstOrDefaultAsync(d => d.Id == docId, ct);
            if (doc == null || string.IsNullOrWhiteSpace(doc.StoragePath))
            {
                logger.LogWarning("Internal document {DocId} not found or missing storage path", docId);
                continue;
            }

            if (!storage.IsConfigured)
                throw new InvalidOperationException("Supabase Storage not configured.");

            var bytes = await storage.DownloadAsync(doc.StoragePath, ct);
            var payload = await internalParse.EnsureParsedAsync(doc, bytes, ct);
            var fileName = payload.FileName ?? doc.OriginalFileName ?? doc.Title ?? "policy.pdf";

            var clauses = await policyClauseExtract.ExtractFromMarkdownAsync(
                payload.FileHash,
                fileName,
                payload.Markdown,
                ct);

            foreach (var clause in clauses)
            {
                allSections.Add(new NdRegulInternalSection
                {
                    AnalysisRunId = run.Id,
                    SectionRef = clause.ClauseNo,
                    SectionText = clause.ClauseText,
                    SourceDoc = fileName,
                    SourcePage = clause.SourcePage > 0 ? clause.SourcePage : null,
                });
            }
        }

        if (allSections.Count == 0)
            throw new InvalidOperationException("No internal policy sections extracted for reverse coverage.");

        db.NdRegulInternalSections.AddRange(allSections);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Extracted {Count} internal sections for run {RunId} via Landing AI ({Schema})",
            allSections.Count,
            run.Id,
            LandingAiPolicyClauseExtractService.PolicyClausesSchemaKey);
    }

    private async Task RunQualitativePhaseAsync(NdAnalysisRun run, CancellationToken ct)
    {
        var row = await db.NdRegulQualitativeAssessments
            .FirstOrDefaultAsync(q => q.AnalysisRunId == run.Id, ct);
        if (row == null)
        {
            row = new NdRegulQualitativeAssessment { AnalysisRunId = run.Id };
            db.NdRegulQualitativeAssessments.Add(row);
        }

        row.Status = "running";
        row.ErrorMessage = null;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        try
        {
            if (runCancellation.IsStopRequested(run.Id)) throw new OperationCanceledException();

            var regulatoryText = await BuildRegulatoryTextAsync(run, ct);
            var policyText = await BuildPolicyContextAsync(run, ct);
            var prompt = string.Join("\n\n", new[]
            {
                NdRegulPromptDefaults.QualitativeAssessmentSystemPrompt.Trim(),
                NdRegulPromptDefaults.BuildQualitativeAssessmentPrompt(regulatoryText, policyText),
                QualitativeJsonInstruction,
            });

            var raw = await regulLlm.AnalyzeTextAsync(prompt, ct);
            var result = NdRegulLlmJsonHelper.ParseJsonObject<RegulQualitativeResult>(raw);

            row.Status = "completed";
            row.ResultJson = JsonSerializer.Serialize(
                result,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            row.ErrorMessage = null;
            row.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            logger.LogInformation("Regul qualitative assessment completed for run {RunId}", run.Id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Regul qualitative assessment failed for run {RunId}", run.Id);
            row.Status = "failed";
            row.ErrorMessage = ex.Message;
            row.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task<string> BuildRegulatoryTextAsync(NdAnalysisRun run, CancellationToken ct)
    {
        var findings = await db.NdRegulForwardFindings
            .Where(f => f.AnalysisRunId == run.Id && f.AnalysisPointId != null)
            .OrderBy(f => f.ClauseNo)
            .ToListAsync(ct);

        if (findings.Count == 0)
        {
            var lines = new List<string>();
            foreach (var point in run.Points.OrderBy(p => p.CreatedAt))
            {
                var (clauseNo, clauseText) = ParseClauseFromSnapshot(point.PointSnapshot);
                if (string.IsNullOrWhiteSpace(clauseNo) && string.IsNullOrWhiteSpace(clauseText)) continue;
                lines.Add($"REGULATORY CLAUSE {clauseNo}:\n{clauseText}");
            }
            return lines.Count == 0
                ? "No regulatory clauses were selected for this run."
                : string.Join("\n\n", lines);
        }

        return string.Join(
            "\n\n",
            findings.Select(f => $"REGULATORY CLAUSE {f.ClauseNo}:\n{f.ClauseText}"));
    }

    private async Task FinalizePointCountsAsync(NdAnalysisRun run, CancellationToken ct)
    {
        var points = await db.NdAnalysisPoints
            .Where(p => p.AnalysisRunId == run.Id)
            .ToListAsync(ct);
        var completed = points.Count(p => p.LandingAiStatus == "completed");
        run.TotalPointsCount = points.Count;
        run.ProcessedPointsCount = completed;
        run.LandingAiCompletedCount = completed;
        run.DualVerifyCompletedCount = completed;
    }

    private async Task MarkCancelledAsync(NdAnalysisRun run, CancellationToken ct)
    {
        run.Status = "cancelled";
        run.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private static (string ClauseNo, string ClauseText) ParseClauseFromSnapshot(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return ("", "");
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var no = root.TryGetProperty("pointNumber", out var pn) ? pn.GetString() ?? ""
                : root.TryGetProperty("point_number", out var pn2) ? pn2.GetString() ?? "" : "";
            var text = root.TryGetProperty("pointText", out var pt) ? pt.GetString() ?? ""
                : root.TryGetProperty("point_text", out var pt2) ? pt2.GetString() ?? ""
                : root.TryGetProperty("pointContent", out var pc) ? pc.GetString() ?? ""
                : root.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
            return (no, text);
        }
        catch
        {
            return ("", "");
        }
    }
}
