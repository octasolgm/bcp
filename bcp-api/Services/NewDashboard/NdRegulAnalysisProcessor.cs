using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.Entities;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Infrastructure.NewDashboard;
using Reguliq.Api.Models;
using Reguliq.Api.Services;
using Reguliq.Api.Services.LandingAi;
using Reguliq.Api.Services.Llm;
using Reguliq.Api.Services.NewDashboard.Demo;
using Reguliq.Api.Services.Pdf;
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
    NdAnalysisPromptVersionService promptVersions,
    NdInternalParseService internalParse,
    NdInternalDocumentSectionService internalSectionService,
    NdRegulationPointRepairService regulationPointRepair,
    SupabaseStorageService storage,
    NdAnalysisRunCancellationTracker runCancellation,
    NdDemoUserDirectory demoDirectory,
    RegulEmbeddingRetrievalService embeddingRetrieval,
    NdLocalDocumentPayloadLoader localPayloadLoader,
    NdGapEvidencePrepareService documentPrepare,
    NdRegulClauseContextService clauseContexts,
    ILogger<NdRegulAnalysisProcessor> logger)
{
    /// <summary>V5: every internal document must be parsed, extracted and indexed before Steps 1-6, or
    /// retrieval silently leaves it out. Documents already indexed under any engine are left as they are;
    /// the rest go through the same azure-di parse → extract → index as uploaded gap evidence.</summary>
    private async Task EnsureInternalDocsIndexedAsync(NdAnalysisRun run, CancellationToken ct)
    {
        var docIds = (JsonSerializer.Deserialize<List<string>>(run.SelectedInternalDocIds) ?? [])
            .Select(s => Guid.TryParse(s, out var g) ? g : (Guid?)null)
            .Where(g => g.HasValue)
            .Select(g => g!.Value)
            .Distinct()
            .ToList();
        if (docIds.Count == 0) return;

        var indexed = await db.NdLocalDocumentExtractions.AsNoTracking()
            .Where(e => docIds.Contains(e.StoredDocumentId) && e.IndexStatus == "indexed")
            .Select(e => e.StoredDocumentId)
            .Distinct()
            .ToListAsync(ct);
        var missing = docIds.Except(indexed).ToList();
        if (missing.Count == 0)
        {
            logger.LogInformation("Regul V5 run {RunId}: all {Count} internal document(s) already indexed", run.Id, docIds.Count);
            return;
        }

        logger.LogInformation(
            "Regul V5 run {RunId}: preparing {Missing} of {Count} internal document(s) (parse → extract → index)",
            run.Id, missing.Count, docIds.Count);
        foreach (var docId in missing)
        {
            if (runCancellation.IsStopRequested(run.Id)) throw new OperationCanceledException();
            try
            {
                await documentPrepare.PrepareDocumentsAsync([docId], run.WorkflowEngine, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    $"Internal document {docId} could not be parsed, extracted and indexed: {ex.GetBaseException().Message}", ex);
            }
        }
    }
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

        if (!AnalysisWorkflowEngine.IsRegulFamily(run.WorkflowEngine))
            throw new InvalidOperationException("Run is not a Regul workflow analysis.");

        if (run.RegulClausesConfirmedAt == null)
            throw new InvalidOperationException(
                "Regul clauses must be confirmed before analysis. Call POST /nd/analysis-runs/{id}/confirm-clauses first.");

        if (await IsDemoOwnedRunAsync(run, ct))
        {
            logger.LogWarning("Refusing live Regul AI for demo-owned run {RunId}", runId);
            return;
        }

        if (runCancellation.IsStopRequested(runId) || run.Status == "cancelled")
        {
            await MarkCancelledAsync(run, ct);
            return;
        }

        var llm = await llmSettings.GetConfigAsync(ct);
        run.RegulLlmProvider = llm.Provider;
        run.RegulLlmModel = llm.Model;
        if (runCancellation.IsStopRequested(runId))
        {
            await MarkCancelledAsync(run, ct);
            return;
        }

        await db.Entry(run).ReloadAsync(ct);
        if (runCancellation.IsStopRequested(runId) || run.Status == "cancelled")
        {
            await MarkCancelledAsync(run, ct);
            return;
        }

        run.Status = "running";
        run.RegulPipelinePhase = "parsing";
        run.RegulPipelineError = null;
        run.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Regul pipeline started for run {RunId} (qualitative={EnableQualitative}, llm={Provider}/{Model})",
            runId,
            run.EnableQualitative,
            run.RegulLlmProvider,
            run.RegulLlmModel);

        var promptVersionsForRun = await promptVersions.GetJudgmentPromptVersionsAsync(run.WorkflowEngine, ct);
        logger.LogInformation(
            "Regul V3 forward judgment for run {RunId} will use the CURRENT admin prompt versions (Admin \u2192 Analysis prompts): {PromptVersions}",
            runId,
            string.Join(
                " | ",
                promptVersionsForRun.Select(v => $"{v.PromptKey}=v{v.VersionNumber} \"{v.Label}\"")));

        try
        {
            await PrepareRegulRunPreForwardAsync(run, ct);
            await EnsureForwardFindingsAsync(run, ct);

            if (AnalysisWorkflowEngine.IsRegulPipelineHybrid(run.WorkflowEngine))
            {
                run.RegulPipelinePhase = "retrieval";
                run.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                logger.LogInformation("Regul pipeline phase=retrieval for run {RunId}", runId);
                await embeddingRetrieval.RunRetrievalAsync(run, ct);
                // Falls through to the normal forward phase below — Step 8's actual LLM call is
                // paused per-clause instead (see the PAUSED block in ExecuteForwardJudgmentAsync),
                // so the rest of the pipeline (context building, per-clause looping, save) still
                // runs for real and is fully testable while consuming zero LLM credit.
            }

            run.RegulPipelinePhase = "forward";
            run.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Regul pipeline phase=forward for run {RunId}", runId);
            await RunForwardPhaseAsync(run, ct);
            if (runCancellation.IsStopRequested(runId))
            {
                await MarkCancelledAsync(run, ct);
                return;
            }

            if (AnalysisWorkflowEngine.IsForwardOnlyFullMarkdown(run.WorkflowEngine))
            {
                run.RegulPipelinePhase = "done";
                run.Status = "completed";
                await FinalizePointCountsAsync(run, ct);
                run.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                logger.LogInformation(
                    "Regul full-markdown run completed for run {RunId} (forward only, no reverse)",
                    runId);
                return;
            }

            run.RegulPipelinePhase = "reverse";
            run.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Regul pipeline phase=reverse for run {RunId}", runId);
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
                logger.LogInformation("Regul pipeline phase=qualitative for run {RunId}", runId);
                await RunQualitativePhaseAsync(run, ct);
            }

            run.RegulPipelinePhase = "done";
            run.Status = "completed";
            await FinalizePointCountsAsync(run, ct);
            run.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Regul pipeline completed for run {RunId} (totalPoints={Total}, processed={Processed})",
                runId,
                run.TotalPointsCount,
                run.ProcessedPointsCount);
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

    /// <summary>Forward judgment only — skips reverse coverage and qualitative phases.</summary>
    public async Task ProcessForwardOnlyRunAsync(Guid runId, CancellationToken ct)
    {
        var run = await db.NdAnalysisRuns
            .Include(r => r.Points)
            .FirstOrDefaultAsync(r => r.Id == runId, ct)
            ?? throw new InvalidOperationException("Analysis run not found.");

        if (!AnalysisWorkflowEngine.IsRegulFamily(run.WorkflowEngine))
            throw new InvalidOperationException("Run is not a Regul workflow analysis.");

        if (run.RegulClausesConfirmedAt == null)
            throw new InvalidOperationException(
                "Regul clauses must be confirmed before analysis. Call POST /nd/analysis-runs/{id}/confirm-clauses first.");

        if (await IsDemoOwnedRunAsync(run, ct))
        {
            logger.LogWarning("Refusing live Regul AI for demo-owned run {RunId}", runId);
            return;
        }

        if (runCancellation.IsStopRequested(runId) || run.Status == "cancelled")
        {
            await MarkCancelledAsync(run, ct);
            return;
        }

        var llm = await llmSettings.GetConfigAsync(ct);
        run.RegulLlmProvider = llm.Provider;
        run.RegulLlmModel = llm.Model;
        if (runCancellation.IsStopRequested(runId))
        {
            await MarkCancelledAsync(run, ct);
            return;
        }

        run.Status = "running";
        run.RegulPipelinePhase = "parsing";
        run.RegulPipelineError = null;
        run.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Regul forward-only run started for run {RunId} (llm={Provider}/{Model})",
            runId,
            run.RegulLlmProvider,
            run.RegulLlmModel);

        try
        {
            await PrepareRegulRunPreForwardAsync(run, ct);
            await EnsureForwardFindingsAsync(run, ct);

            if (AnalysisWorkflowEngine.IsRegulPipelineHybrid(run.WorkflowEngine))
            {
                run.RegulPipelinePhase = "retrieval";
                run.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                logger.LogInformation("Regul pipeline phase=retrieval for run {RunId}", runId);
                await embeddingRetrieval.RunRetrievalAsync(run, ct);
                // Falls through to the normal forward phase below — see the matching comment in
                // ProcessRunAsync for why (Step 8's LLM call is paused per-clause instead).
            }

            run.RegulPipelinePhase = "forward";
            run.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await RunForwardPhaseAsync(run, ct);
            if (runCancellation.IsStopRequested(runId))
            {
                await MarkCancelledAsync(run, ct);
                return;
            }

            run.RegulPipelinePhase = "done";
            run.Status = "completed";
            await FinalizePointCountsAsync(run, ct);
            run.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Regul forward-only run completed for run {RunId} (totalPoints={Total}, processed={Processed})",
                runId,
                run.TotalPointsCount,
                run.ProcessedPointsCount);
        }
        catch (OperationCanceledException)
        {
            await MarkCancelledAsync(run, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Regul forward-only run failed for run {RunId}", runId);
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

        // Hybrid engine (V5) points come from local structural chunking, not the legacy
        // NdRegulationPoint table — their id is a synthetic "{regDocId}:{clauseNo}" string, which
        // never parses as a Guid, so RegulationPointId stays null for every one of them (see
        // AnalysisRunsController's point-creation Guid.TryParse). Gating on RegulationPointId.
        // HasValue like every other engine does would silently create zero findings for every V5
        // run. This engine's whole retrieval pipeline only ever reads PointSnapshot/ClauseText —
        // it has no use for a real NdRegulationPoint row — so a non-empty snapshot is sufficient.
        var isHybrid = AnalysisWorkflowEngine.IsRegulPipelineHybrid(run.WorkflowEngine);
        var eligiblePoints = run.Points.Where(p =>
            p.RegulationPointId.HasValue || (isHybrid && !string.IsNullOrWhiteSpace(p.PointSnapshot)));
        foreach (var point in eligiblePoints)
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

    /// <summary>How many clauses' judgment LLM calls run at once during the forward phase. Every DB
    /// read a clause's judgment call needs (retrieval chunks, prompt templates, provider config) is
    /// resolved up front in a cheap sequential prep pass (PrepareForwardJudgmentAsync) — the only
    /// thing that runs concurrently is the actual outbound LLM call (ExecuteForwardJudgmentAsync),
    /// which touches no shared state, so raising this is safe up to whatever the configured LLM
    /// provider can actually take concurrently. Admin-tunable without a redeploy in case a given
    /// provider/model needs a lower ceiling.</summary>
    private static readonly int ForwardJudgmentConcurrency = Math.Max(
        1,
        int.TryParse(Environment.GetEnvironmentVariable("BCP_REGUL_FORWARD_JUDGMENT_CONCURRENCY"), out var configuredConcurrency)
            ? configuredConcurrency
            : 8);

    public sealed record ForwardJudgmentPrep(
        NdRegulForwardFinding Finding,
        NdAnalysisPoint Point,
        int Index,
        NdRegulPolicyContextService.PolicyBundle ClauseBundle,
        string ContextBlock,
        string QueryBlock,
        IReadOnlyList<NdRegulPolicyContextService.PolicyChunk> ContextChunks,
        DualVerifyLlmConfig Config,
        string SystemPrompt,
        bool CacheContextBlock,
        bool IsHybridEngine,
        string? WorkflowEngine)
    {
        /// <summary>Step 7/8 audit rows for this clause. Filled in memory while the LLM call runs (no DB
        /// access in phase 2) and saved together with the clause's result.</summary>
        public List<NdRegulClauseTrace> Traces { get; } = [];
    }

    private async Task RunForwardPhaseAsync(NdAnalysisRun run, CancellationToken ct, string traceSource = RegulClauseTraceSources.Analysis)
    {
        var policyMode = NdRegulPolicyContextService.ResolveMode(run.WorkflowEngine);
        var policyBundle = await LoadPolicyBundleAsync(run, policyMode, ct);
        var pending = await db.NdRegulForwardFindings
            .Where(f => f.AnalysisRunId == run.Id
                && f.Status == "pending"
                && f.AnalysisPointId != null
                && !f.ClauseNo.StartsWith(NdRegulReverseIntRows.IntClausePrefix))
            .ToListAsync(ct);

        var pointById = run.Points.ToDictionary(p => p.Id);
        var completed = 0;
        var total = pending.Count;
        var cacheContext = policyBundle.UsesFullMarkdown;

        var promptVersionsInUse = await promptVersions.GetJudgmentPromptVersionsAsync(run.WorkflowEngine, ct);
        run.RegulPromptVersions = NdAnalysisEvalService.DescribePromptVersions(
            promptVersionsInUse.Select(v => new NdAnalysisEvalService.PromptVersionRef(v.PromptKey, v.VersionNumber, v.Label)));
        logger.LogInformation(
            "Regul forward phase using admin prompt versions for run {RunId}: {PromptVersions}",
            run.Id,
            string.Join(
                ", ",
                promptVersionsInUse.Select(v => $"{v.PromptKey}=v{v.VersionNumber} ({v.Label})")));

        logger.LogInformation(
            "Regul forward phase started for run {RunId}: {Total} clause(s), policyPages={Pages}, retrieval={Retrieval}, fullMarkdownFiles={FileCount}, fullMarkdownChars={Chars}, concurrency={Concurrency}",
            run.Id,
            total,
            policyBundle.TotalPages,
            !policyBundle.UsesFullMarkdown,
            policyBundle.MarkdownByFile.Count,
            policyBundle.SourceTextForQuotes.Length,
            ForwardJudgmentConcurrency);

        // Phase 1 — sequential prep: every DB read a clause's judgment call needs (retrieval chunks,
        // prompt templates, provider config) resolved up front. Cheap (no LLM calls here), so doing
        // this one clause at a time costs nothing, and it means phase 2 below never touches the
        // database, so nothing races on the shared DbContext when several clauses run at once.
        var preps = new List<ForwardJudgmentPrep>(pending.Count);
        for (var i = 0; i < pending.Count; i++)
        {
            var finding = pending[i];
            if (runCancellation.IsStopRequested(run.Id)) throw new OperationCanceledException();
            if (!finding.AnalysisPointId.HasValue || !pointById.TryGetValue(finding.AnalysisPointId.Value, out var point))
                continue;

            var index = i + 1;
            point.LandingAiStatus = "running";
            point.LandingAiError = null;
            point.UpdatedAt = DateTimeOffset.UtcNow;
            finding.Status = "running";
            finding.UpdatedAt = DateTimeOffset.UtcNow;
            run.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            // V5 hybrid engine: use this clause's own Step 1+3+4 retrieval output instead of
            // the run-wide bundle every other engine shares — same LLM call, same prompt,
            // different context, per finding.RetrievalJson (see BuildRetrievalPolicyBundleAsync).
            var clauseBundle = AnalysisWorkflowEngine.IsRegulPipelineHybrid(run.WorkflowEngine)
                ? await BuildRetrievalPolicyBundleAsync(finding, ct)
                : policyBundle;

            preps.Add(await PrepareForwardJudgmentAsync(finding, point, index, clauseBundle, cacheContext, run.WorkflowEngine, ct));
        }

        // Phase 2 — bounded-concurrency execute: only the outbound LLM call runs in parallel. Each clause
        // is finalized and saved the moment its own call returns (under dbLock, so DB writes never
        // overlap on the shared DbContext) — the UI sees clauses complete one by one instead of all
        // at once after the slowest call.
        using var gate = new SemaphoreSlim(ForwardJudgmentConcurrency);
        using var dbLock = new SemaphoreSlim(1, 1);
        var cancelled = false;
        var executions = preps.Select(async prep =>
        {
            RegulJudgmentResult? judgment = null;
            Exception? error = null;
            await gate.WaitAsync(ct);
            try
            {
                logger.LogInformation(
                    "Regul forward judgment started for run {RunId} clause {ClauseNo} ({Index}/{Total})",
                    run.Id, prep.Finding.ClauseNo, prep.Index, total);
                judgment = await ExecuteForwardJudgmentAsync(prep, ct);
                if (runCancellation.IsStopRequested(run.Id) || ct.IsCancellationRequested)
                    throw new OperationCanceledException();
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                gate.Release();
            }

            await dbLock.WaitAsync(CancellationToken.None);
            try
            {
                if (FinalizeForwardJudgment(prep, judgment, error))
                    cancelled = true;
                else if (error == null)
                    completed++;
                db.NdRegulClauseTraces.AddRange(WithSource(prep.Traces, traceSource));

                run.ProcessedPointsCount = completed;
                run.LandingAiCompletedCount = completed;
                run.DualVerifyCompletedCount = completed;
                run.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(CancellationToken.None);
            }
            finally
            {
                dbLock.Release();
            }
        }).ToList();

        await Task.WhenAll(executions);

        logger.LogInformation(
            "Regul forward phase completed for run {RunId} ({Completed}/{Total}, policyPages={Pages}, retrieval={Retrieval})",
            run.Id,
            completed,
            pending.Count,
            policyBundle.TotalPages,
            !policyBundle.UsesFullMarkdown);

        if (cancelled) throw new OperationCanceledException();
    }

    /// <summary>Applies one clause's judgment outcome to its finding + point. Returns true when the
    /// clause was cancelled. Caller saves.</summary>
    private bool FinalizeForwardJudgment(ForwardJudgmentPrep prep, RegulJudgmentResult? judgment, Exception? error)
    {
        var finding = prep.Finding;
        var point = prep.Point;

        if (error is OperationCanceledException)
        {
            finding.Status = "cancelled";
            finding.ErrorMessage = "Stopped by user";
            finding.UpdatedAt = DateTimeOffset.UtcNow;
            if (point.LandingAiStatus is "pending" or "running")
            {
                point.LandingAiStatus = "cancelled";
                point.LandingAiError = "Stopped by user";
                point.DualVerifyStatus = "skipped";
                point.UpdatedAt = DateTimeOffset.UtcNow;
            }
            return true;
        }

        if (error != null)
        {
            logger.LogError(error, "Regul forward judgment failed for run {RunId} clause {ClauseNo} (#{Index})",
                finding.AnalysisRunId, finding.ClauseNo, prep.Index);
            finding.Status = "failed";
            finding.ErrorMessage = error.Message;
            finding.UpdatedAt = DateTimeOffset.UtcNow;
            point.LandingAiStatus = "failed";
            point.LandingAiError = error.Message;
            point.UpdatedAt = DateTimeOffset.UtcNow;
            return false;
        }

        var landingMessage = NdRegulJudgmentFormatter.FormatLandingMessage(
            finding.ClauseNo, finding.ClauseText, judgment!);

        finding.Status = "completed";
        finding.ResultJson = JsonSerializer.Serialize(judgment);
        finding.ErrorMessage = null;
        finding.UpdatedAt = DateTimeOffset.UtcNow;

        NdRegulAnalysisPointSync.ApplyForwardJudgment(point, judgment!, landingMessage);
        logger.LogInformation(
            "Regul forward judgment completed for run {RunId} clause {ClauseNo} (#{Index}) status={Status} confidence={Confidence} policyExtracts={ExtractCount}",
            finding.AnalysisRunId,
            finding.ClauseNo,
            prep.Index,
            judgment!.OverallStatus,
            judgment.Confidence,
            judgment.PolicyExtract.Count);
        return false;
    }

    // camelCase — RetrievalJson was written with this same policy (see RegulEmbeddingRetrievalService).
    private static readonly JsonSerializerOptions RetrievalJsonReadOptions =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Builds this one clause's judgment context straight from its own Step 1+3+4
    /// retrieval output (finding.RetrievalJson) instead of the run-wide bundle — the actual
    /// point of Step 8 being "retrieval-aware". The stored preview only carries a truncated
    /// TextPreview (kept small for the UI panel), so this re-fetches each matched section's real
    /// full text before handing it to the LLM. Embedding and BM25 matches are deduped by section
    /// id (embedding first — typically the more precise signal); if RetrievalJson is missing or
    /// empty (e.g. no internal docs were indexed for this clause), returns an empty bundle rather
    /// than silently falling back to full markdown — an empty context is a visible, honest signal
    /// that retrieval found nothing, not a hidden cost regression back to sending everything.</summary>
    private async Task<NdRegulPolicyContextService.PolicyBundle> BuildRetrievalPolicyBundleAsync(
        NdRegulForwardFinding finding, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(finding.RetrievalJson))
            return NdRegulPolicyContextService.FromRetrievalChunks([]);

        RegulEmbeddingRetrievalService.RetrievalPreview? preview;
        try
        {
            preview = JsonSerializer.Deserialize<RegulEmbeddingRetrievalService.RetrievalPreview>(
                finding.RetrievalJson, RetrievalJsonReadOptions);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Malformed RetrievalJson for finding {FindingId} — using empty context", finding.Id);
            return NdRegulPolicyContextService.FromRetrievalChunks([]);
        }

        return await BuildBundleFromPreviewAsync(preview, ct);
    }

    private async Task<NdRegulPolicyContextService.PolicyBundle> BuildBundleFromPreviewAsync(
        RegulEmbeddingRetrievalService.RetrievalPreview? preview, CancellationToken ct)
    {
        if (preview == null || (preview.Matches.Count == 0 && preview.Bm25Matches.Count == 0))
            return NdRegulPolicyContextService.FromRetrievalChunks([]);

        NdRegulPolicyContextService.PolicyChunk ToChunk(Guid sectionId, string? clauseNo, string textPreview, string? sourceDocumentName, int? sourcePage, Dictionary<Guid, string> fullTextById)
        {
            var text = fullTextById.GetValueOrDefault(sectionId, textPreview);
            var docLabel = sourceDocumentName ?? "internal policy";
            var refLabel = string.IsNullOrWhiteSpace(clauseNo) ? "" : $" — {clauseNo}";
            var pageLabel = sourcePage.HasValue ? $" p.{sourcePage}" : "";
            return new NdRegulPolicyContextService.PolicyChunk(
                $"{docLabel}{refLabel}{pageLabel}", text, sourceDocumentName, clauseNo, sourcePage);
        }

        var chunks = new List<NdRegulPolicyContextService.PolicyChunk>();

        // Step 5/6 — preferred path: the already-fused, already-trimmed ranked list. Falls back to
        // the raw union (old behavior) only for a RetrievalJson row saved before fusion existed,
        // where FusedMatches is null.
        if (preview.FusedMatches is { Count: > 0 } fused)
        {
            var fusedSectionIds = fused.Select(m => m.SectionId).ToList();
            var fusedFullTextById = await db.NdLocalDocumentExtractionSections
                .AsNoTracking()
                .Where(s => fusedSectionIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, s => s.ClauseText, ct);

            foreach (var m in fused)
                chunks.Add(ToChunk(m.SectionId, m.ClauseNo, m.TextPreview, m.SourceDocumentName, m.SourcePage, fusedFullTextById));

            var fusedDocIds = fused.Select(m => m.SourceDocumentId).Distinct().ToList();
            var fusedMarkdown = await LoadParsedMarkdownByDocumentNameAsync(fusedDocIds, ct);
            return NdRegulPolicyContextService.FromRetrievalChunks(chunks, fusedMarkdown);
        }

        var sectionIds = preview.Matches.Select(m => m.SectionId)
            .Concat(preview.Bm25Matches.Select(m => m.SectionId))
            .Distinct()
            .ToList();
        var fullTextById = await db.NdLocalDocumentExtractionSections
            .AsNoTracking()
            .Where(s => sectionIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.ClauseText, ct);

        var seen = new HashSet<Guid>();
        foreach (var m in preview.Matches)
        {
            if (!seen.Add(m.SectionId)) continue;
            chunks.Add(ToChunk(m.SectionId, m.ClauseNo, m.TextPreview, m.SourceDocumentName, m.SourcePage, fullTextById));
        }
        foreach (var m in preview.Bm25Matches)
        {
            if (!seen.Add(m.SectionId)) continue;
            chunks.Add(ToChunk(m.SectionId, m.ClauseNo, m.TextPreview, m.SourceDocumentName, m.SourcePage, fullTextById));
        }

        var docIds = preview.Matches.Select(m => m.SourceDocumentId)
            .Concat(preview.Bm25Matches.Select(m => m.SourceDocumentId))
            .Distinct()
            .ToList();
        var markdownByFile = await LoadParsedMarkdownByDocumentNameAsync(docIds, ct);
        return NdRegulPolicyContextService.FromRetrievalChunks(chunks, markdownByFile);
    }

    /// <summary>Parsed markdown per document for this job (the processor is scoped to one run/job), so
    /// each clause doesn't re-download every document's full text just to resolve quote pages.</summary>
    private readonly Dictionary<Guid, (string Name, string? Markdown)> _markdownByDocId = new();

    private async Task<Dictionary<string, string>> LoadParsedMarkdownByDocumentNameAsync(
        IReadOnlyCollection<Guid> storedDocumentIds,
        CancellationToken ct)
    {
        var missing = storedDocumentIds.Where(id => !_markdownByDocId.ContainsKey(id)).Distinct().ToList();
        if (missing.Count > 0)
        {
            var docs = await db.StoredDocuments.AsNoTracking()
                .Where(d => missing.Contains(d.Id))
                .Select(d => new { d.Id, Name = d.OriginalFileName ?? d.Title ?? "document" })
                .ToListAsync(ct);

            var rows = await db.NdLocalDocumentExtractions.AsNoTracking()
                .Where(e => missing.Contains(e.StoredDocumentId)
                            && e.Status == "parsed"
                            && e.MarkdownText != null)
                .OrderByDescending(e => e.ParsedAt)
                .Select(e => new { e.StoredDocumentId, e.MarkdownText })
                .ToListAsync(ct);

            foreach (var doc in docs)
                _markdownByDocId[doc.Id] = (doc.Name, rows.FirstOrDefault(r => r.StoredDocumentId == doc.Id)?.MarkdownText);
        }

        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in storedDocumentIds)
        {
            if (_markdownByDocId.TryGetValue(id, out var entry) && entry.Markdown is { Length: > 0 } md)
                dict[entry.Name] = md;
        }

        return dict;
    }

    /// <summary>
    /// One clause's judgment exactly as the analysis run makes it — same retrieval on the hybrid
    /// engine, same admin prompt versions, same post-processing — but over the run's internal
    /// documents plus extra ones (uploaded gap evidence). Prepares only; nothing is saved, so the
    /// clause's original judgment stays on record. Run the returned prep with
    /// <see cref="ExecuteClauseJudgmentAsync"/>, which touches no database and is safe in parallel.
    /// </summary>
    public async Task<(ForwardJudgmentPrep Prep, RegulEmbeddingRetrievalService.RetrievalPreview? Retrieval)> PrepareClauseJudgmentWithExtraDocsAsync(
        NdAnalysisRun run,
        NdRegulForwardFinding finding,
        NdAnalysisPoint point,
        IReadOnlyCollection<Guid> extraDocIds,
        CancellationToken ct)
    {
        var internalIds = (JsonSerializer.Deserialize<List<string>>(run.SelectedInternalDocIds) ?? [])
            .Select(s => Guid.TryParse(s, out var g) ? g : (Guid?)null)
            .Where(g => g.HasValue)
            .Select(g => g!.Value);
        var corpus = internalIds.Concat(extraDocIds).Distinct().ToList();

        NdRegulPolicyContextService.PolicyBundle bundle;
        RegulEmbeddingRetrievalService.RetrievalPreview? preview = null;
        if (AnalysisWorkflowEngine.IsRegulPipelineHybrid(run.WorkflowEngine))
        {
            preview = await embeddingRetrieval.RetrievePreviewForClauseAsync(corpus, finding.ClauseText, ct);
            bundle = await BuildBundleFromPreviewAsync(preview, ct);
        }
        else
        {
            var payloads = await LoadInternalDocPayloadsAsync(corpus.Select(id => id.ToString()).ToList(), ct);
            bundle = NdRegulPolicyContextService.FromPayloads(
                payloads,
                NdRegulPolicyContextService.ResolveMode(run.WorkflowEngine));
        }

        var prep = await PrepareForwardJudgmentAsync(finding, point, 1, bundle, bundle.UsesFullMarkdown, run.WorkflowEngine, ct);
        return (prep, preview);
    }

    public Task<RegulJudgmentResult> ExecuteClauseJudgmentAsync(ForwardJudgmentPrep prep, CancellationToken ct) =>
        ExecuteForwardJudgmentAsync(prep, ct);

    /// <summary>Phase 1 of forward judgment: resolves every DB-backed input a clause's judgment call
    /// needs (retrieval context, prompt templates, provider config) so phase 2 (ExecuteForwardJudgmentAsync)
    /// can run several clauses concurrently touching only the network, never the database. Same context/
    /// query construction CallForwardJudgmentAsync used to do inline — unchanged, just resolved up front.</summary>
    private async Task<ForwardJudgmentPrep> PrepareForwardJudgmentAsync(
        NdRegulForwardFinding finding,
        NdAnalysisPoint point,
        int index,
        NdRegulPolicyContextService.PolicyBundle policyBundle,
        bool cacheContextBlock,
        string? workflowEngine,
        CancellationToken ct)
    {
        var clauseNo = finding.ClauseNo;
        var clauseText = finding.ClauseText;
        var policyContext = policyBundle.BuildContextForClause(clauseText);
        var contextChunks = policyBundle.GetChunksForClause(clauseText);
        if (!policyBundle.UsesFullMarkdown)
        {
            logger.LogDebug(
                "Regul forward retrieval for clause {ClauseNo}: {ChunkCount} chunk(s) — {ChunkLabels}",
                clauseNo,
                contextChunks.Count,
                string.Join("; ", contextChunks.Select(c => c.Label)));
        }
        logger.LogInformation(
            "Regul judgment context for clause {ClauseNo}: {ChunkCount} chunk(s), {ContextChars} chars (~{ApproxTokens} tokens)",
            clauseNo,
            contextChunks.Count,
            policyContext.Length,
            policyContext.Length / 4);
        var contextBlock = await promptVersions.BuildJudgmentContextAsync(policyContext, workflowEngine, ct);
        // Supporting regulatory context (parent, sibling and sub-clause headings). Built for every clause so
        // it is always logged; only sent when the current user block 2 has {clause_context} (v9+).
        var clauseContext = await clauseContexts.BuildAsync(point, clauseNo, ct);
        var clauseContextSent = await promptVersions.CurrentQueryUsesClauseContextAsync(workflowEngine, ct);
        var queryBlock = await promptVersions.BuildJudgmentQueryAsync(clauseNo, clauseText, workflowEngine, ct, clauseContext?.Text);
        var (cfg, systemPrompt, resolvedCacheContextBlock, isHybridEngine) =
            await regulLlm.ResolveJudgmentCallInputsAsync(cacheContextBlock, workflowEngine, ct);

        var prep = new ForwardJudgmentPrep(
            finding,
            point,
            index,
            policyBundle,
            contextBlock,
            queryBlock,
            contextChunks,
            cfg,
            systemPrompt,
            resolvedCacheContextBlock,
            isHybridEngine,
            workflowEngine);

        // Step 7 — exactly what the judgment call will be given as policy context.
        prep.Traces.Add(new NdRegulClauseTrace
        {
            AnalysisRunId = finding.AnalysisRunId,
            FindingId = finding.Id,
            ClauseNo = clauseNo,
            Step = RegulClauseTraceSteps.Context,
            ContextText = contextBlock,
            ChunksJson = JsonSerializer.Serialize(contextChunks.Select(c => new
            {
                label = c.Label,
                sourceDocument = c.SourceDoc,
                page = c.SourcePage,
                chars = c.Text.Length,
            })),
            CharsSent = contextBlock.Length,
            Notes = $"{contextChunks.Count} chunk(s) from the clause's Step 6 selection, {contextBlock.Length} chars; "
                + DescribeClauseContext(clauseContext, clauseContextSent),
            ClauseContext = clauseContext?.Text,
            ClauseContextSent = clauseContextSent,
            TenantId = finding.TenantId,
        });
        logger.LogInformation(
            "Regul Step 7 supporting regulatory context for clause {ClauseNo}: {Description}{NewLine}{ClauseContext}",
            clauseNo, DescribeClauseContext(clauseContext, clauseContextSent), Environment.NewLine,
            clauseContext?.Text ?? "(none)");
        logger.LogInformation(
            "Regul Step 7 for clause {ClauseNo}: context built from {ChunkCount} retrieved chunk(s), {Chars} chars — {Labels}",
            clauseNo, contextChunks.Count, contextBlock.Length,
            string.Join(" | ", contextChunks.Select(c => c.Label)));

        return prep;
    }

    /// <summary>Phase 2 of forward judgment: the actual LLM call(s) for one clause, including the same
    /// gap-description retry loop CallForwardJudgmentAsync used to run — everything here is either a pure
    /// network call or in-memory post-processing, so it's safe to run for several clauses at once (see
    /// RunForwardPhaseAsync's bounded-concurrency phase 2, which is the whole point of this split).</summary>
    /// <summary>How many times one judgment request is sent before the clause is marked failed.</summary>
    private const int MaxDeliveryAttempts = 3;

    private async Task<RegulJudgmentResult> ExecuteForwardJudgmentAsync(ForwardJudgmentPrep prep, CancellationToken ct)
    {
        var policyBundle = prep.ClauseBundle;
        var contextChunks = prep.ContextChunks;
        var workflowEngine = prep.WorkflowEngine;

        // Step 8 is live for the hybrid engine: same admin-configured LLM (regulLlm) and same
        // admin prompt versions as every other Regul engine — the only difference is the context
        // block, which for V5 is the fused/trimmed retrieval chunks (Step 7) instead of full markdown.
        RegulJudgmentResult judgment = null!;
        for (var attempt = 0; attempt <= NdRegulJudgmentPostProcessor.MaxGapDescriptionRetries; attempt++)
        {
            var query = attempt == 0
                ? prep.QueryBlock
                : prep.QueryBlock + "\n\n" + (prep.IsHybridEngine
                    ? NdRegulPromptDefaults.BuildHybridJudgmentRetryNote(
                        judgment.OverallStatus,
                        string.IsNullOrWhiteSpace(judgment.GapDescription) || judgment.GapDescription.Trim() == "N/A",
                        string.IsNullOrWhiteSpace(judgment.SuggestedAction) || judgment.SuggestedAction.Trim() == "N/A")
                    : NdRegulPromptDefaults.BuildJudgmentRetryNote(judgment.OverallStatus));

            // Delivery retries: a provider error, an answer cut off at the token limit, or unreadable JSON
            // re-sends the same request (same prompt, same reasoning effort) instead of failing the clause.
            NdRegulClauseTrace callTrace = null!;
            for (var delivery = 1; ; delivery++)
            {
                callTrace = new NdRegulClauseTrace
                {
                    AnalysisRunId = prep.Finding.AnalysisRunId,
                    FindingId = prep.Finding.Id,
                    ClauseNo = prep.Finding.ClauseNo,
                    Step = RegulClauseTraceSteps.LlmCall,
                    Attempt = prep.Traces.Count(t => t.Step == RegulClauseTraceSteps.LlmCall) + 1,
                    Provider = prep.Config.Provider,
                    Model = prep.Config.Model,
                    SystemPrompt = attempt == 0 && delivery == 1 ? prep.SystemPrompt : null,
                    QueryText = query,
                    CharsSent = prep.SystemPrompt.Length + prep.ContextBlock.Length + query.Length,
                    TenantId = prep.Finding.TenantId,
                };
                prep.Traces.Add(callTrace);
                var started = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var raw = await regulLlm.DispatchJudgmentAsync(
                        prep.Config, prep.SystemPrompt, prep.ContextBlock, query, prep.CacheContextBlock, prep.IsHybridEngine, ct);
                    callTrace.DurationMs = (int)started.ElapsedMilliseconds;
                    callTrace.ResponseText = raw;
                    logger.LogInformation(
                        "Regul Step 8 call for clause {ClauseNo} attempt {Attempt}: {Provider}/{Model}, sent {CharsSent} chars, received {CharsReceived} chars in {Ms} ms",
                        prep.Finding.ClauseNo, callTrace.Attempt, prep.Config.Provider, prep.Config.Model,
                        callTrace.CharsSent, raw.Length, callTrace.DurationMs);
                    try
                    {
                        judgment = NdRegulLlmJsonHelper.ParseJudgmentResult(raw);
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException($"Could not parse the response: {ex.Message}", ex);
                    }
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested)
                {
                    callTrace.DurationMs ??= (int)started.ElapsedMilliseconds;
                    callTrace.Error = ex.Message;
                    if (delivery >= MaxDeliveryAttempts)
                        throw new InvalidOperationException(
                            $"The AI call failed {MaxDeliveryAttempts} times for this clause. Last error: {ex.Message}", ex);
                    callTrace.Notes = $"retrying the same request ({delivery}/{MaxDeliveryAttempts - 1})";
                    logger.LogWarning(ex,
                        "Regul Step 8 call for clause {ClauseNo} failed (delivery {Delivery}/{Max}); retrying the same request",
                        prep.Finding.ClauseNo, delivery, MaxDeliveryAttempts);
                    await Task.Delay(TimeSpan.FromSeconds(2 * delivery), ct);
                }
            }
            var parsedStatus = judgment.OverallStatus;
            var parsedQuotes = judgment.PolicyExtract.Count;

            judgment = prep.IsHybridEngine
                ? NdRegulJudgmentPostProcessor.ApplyQuoteVerificationHybrid(judgment, policyBundle.SourceTextForQuotes)
                : NdRegulJudgmentPostProcessor.ApplyQuoteVerification(judgment, policyBundle.SourceTextForQuotes);

            judgment = NdRegulJudgmentPostProcessor.ApplyGroundedDocumentReference(
                judgment,
                contextChunks,
                policyBundle.MarkdownByFile);

            if (AnalysisWorkflowEngine.IsForwardOnlyFullMarkdown(workflowEngine))
            {
                judgment = NdRegulJudgmentPostProcessor.ApplyFalseAbsenceCorrection(
                    judgment,
                    policyBundle.SourceTextForQuotes);
            }

            if (prep.IsHybridEngine)
            {
                // V5 only: compliant => no gap / no action; gap => must also have an action plan.
                judgment = NdRegulJudgmentPostProcessor.ApplyStatusConsistency(judgment);
                var needsRetry = NdRegulJudgmentPostProcessor.RequiresGapOrActionRetry(judgment);
                callTrace.Notes = DescribePostProcessing(parsedStatus, parsedQuotes, judgment, needsRetry, attempt);
                if (!needsRetry)
                    return RecordFinal(prep, judgment);
                if (attempt >= NdRegulJudgmentPostProcessor.MaxGapDescriptionRetries)
                    return RecordFinal(prep, NdRegulJudgmentPostProcessor.EnsureActionPlanForGap(judgment));
                continue;
            }

            var needsGapRetry = NdRegulJudgmentPostProcessor.RequiresGapDescriptionRetry(judgment);
            callTrace.Notes = DescribePostProcessing(parsedStatus, parsedQuotes, judgment, needsGapRetry, attempt);
            if (!needsGapRetry)
                return RecordFinal(prep, judgment);

            if (attempt >= NdRegulJudgmentPostProcessor.MaxGapDescriptionRetries)
                return RecordFinal(prep, judgment);
        }

        return RecordFinal(prep, judgment);
    }

    private static string DescribePostProcessing(
        string modelStatus, int modelQuotes, RegulJudgmentResult after, bool retrying, int attempt)
    {
        var notes = new List<string> { $"model status: {modelStatus}, quotes: {modelQuotes}" };
        if (!string.Equals(modelStatus?.Trim(), after.OverallStatus?.Trim(), StringComparison.OrdinalIgnoreCase))
            notes.Add($"status changed by post-processing to: {after.OverallStatus}");
        if (after.PolicyExtract.Count != modelQuotes)
            notes.Add($"{modelQuotes - after.PolicyExtract.Count} quote(s) dropped (not found verbatim in the documents)");
        notes.Add(retrying
            ? $"retry requested: gap and/or action missing for a {after.OverallStatus} verdict (attempt {attempt + 1})"
            : "accepted");
        return string.Join("; ", notes);
    }

    private static string DescribeClauseContext(NdRegulClauseContextService.ClauseContext? context, bool sent)
    {
        if (context == null)
            return sent
                ? "supporting regulatory context: none available for this clause (prompt told so)"
                : "supporting regulatory context: none available";
        var counts = $"{context.Ancestors.Count} parent, {context.Siblings.Count - 1} sibling, {context.Children.Count} sub-clause heading(s), {context.Text.Length} chars";
        return sent
            ? $"supporting regulatory context sent: {counts}"
            : $"supporting regulatory context built but NOT sent (current prompt has no {{clause_context}}): {counts}";
    }

    public static IEnumerable<NdRegulClauseTrace> WithSource(IEnumerable<NdRegulClauseTrace> traces, string source)
    {
        foreach (var t in traces)
        {
            t.Source = source;
            yield return t;
        }
    }

    private static RegulJudgmentResult RecordFinal(ForwardJudgmentPrep prep, RegulJudgmentResult judgment)
    {
        prep.Traces.Add(new NdRegulClauseTrace
        {
            AnalysisRunId = prep.Finding.AnalysisRunId,
            FindingId = prep.Finding.Id,
            ClauseNo = prep.Finding.ClauseNo,
            Step = RegulClauseTraceSteps.PostProcess,
            ResultJson = JsonSerializer.Serialize(judgment, new JsonSerializerOptions { WriteIndented = true }),
            Notes = $"final status: {judgment.OverallStatus}, confidence: {judgment.Confidence:0.00}, quotes kept: {judgment.PolicyExtract.Count}",
            TenantId = prep.Finding.TenantId,
        });
        return judgment;
    }

    private async Task<NdRegulPolicyContextService.PolicyBundle> LoadPolicyBundleAsync(
        NdAnalysisRun run,
        NdRegulPolicyContextService.RegulPolicyContextMode mode,
        CancellationToken ct)
    {
        if (AnalysisWorkflowEngine.IsRegulPipelineHybrid(run.WorkflowEngine))
        {
            // V5: this run-level bundle is only used for logging/cacheContext here — the actual
            // per-clause judgment context comes from BuildRetrievalPolicyBundleAsync (built from
            // Step 1+3+4 retrieval output). Skip the legacy Landing AI-based full-document parse
            // below entirely — it's a separate paid service unrelated to the judgment call, not
            // needed for retrieval-based context, and must never be called for this engine.
            return NdRegulPolicyContextService.FromRetrievalChunks([]);
        }

        var internalDocIds = JsonSerializer.Deserialize<List<string>>(run.SelectedInternalDocIds) ?? [];
        var payloads = await LoadInternalDocPayloadsAsync(internalDocIds, ct);

        if (AnalysisWorkflowEngine.IsForwardOnlyFullMarkdown(run.WorkflowEngine))
        {
            if (payloads.Count == 0)
            {
                throw new InvalidOperationException(
                    "Full-markdown Regul analysis requires parsed internal documents. " +
                    "Attach at least one internal file and run parse before starting analysis.");
            }

            var fullMarkdown = NdRegulPolicyContextService.FromPayloads(
                payloads,
                NdRegulPolicyContextService.RegulPolicyContextMode.FullMarkdown);

            logger.LogInformation(
                "Regul full-markdown bundle for run {RunId}: {FileCount} internal file(s), {TotalPages} total pages, {Chars} chars",
                run.Id,
                fullMarkdown.MarkdownByFile.Count,
                fullMarkdown.TotalPages,
                fullMarkdown.SourceTextForQuotes.Length);

            return fullMarkdown;
        }

        var existing = await db.NdRegulInternalSections
            .AsNoTracking()
            .Where(s => s.AnalysisRunId == run.Id)
            .ToListAsync(ct);

        if (existing.Count > 0)
        {
            var sections = PointNumberSort.OrderByPointNumber(existing, s => s.SectionRef).ToList();
            logger.LogDebug(
                "Building forward policy bundle from {Count} internal section(s) for run {RunId} (mode={Mode})",
                sections.Count,
                run.Id,
                mode);
            return NdRegulPolicyContextService.FromInternalSections(sections, mode)
                .WithMarkdownFromPayloads(payloads);
        }

        if (payloads.Count == 0)
            return NdRegulPolicyContextService.FromPayloads([
                new InternalDocPayload("", "policy", "No internal policy text was attached to this run.", null),
            ], mode);

        return NdRegulPolicyContextService.FromPayloads(payloads, mode);
    }

    private async Task<NdRegulPolicyContextService.PolicyBundle> LoadPolicyBundleAsync(
        NdAnalysisRun run,
        CancellationToken ct) =>
        await LoadPolicyBundleAsync(
            run,
            NdRegulPolicyContextService.ResolveMode(run.WorkflowEngine),
            ct);

    private async Task<string> BuildPolicyContextAsync(NdAnalysisRun run, CancellationToken ct)
    {
        var bundle = await LoadPolicyBundleAsync(run, ct);
        return bundle.BuildFullContext();
    }

    private async Task<List<InternalDocPayload>> LoadInternalDocPayloadsAsync(
        List<string> internalDocIds,
        CancellationToken ct)
    {
        var result = new List<InternalDocPayload>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var idStr in internalDocIds)
        {
            if (!Guid.TryParse(idStr, out var docId)) continue;
            var doc = await db.StoredDocuments.FirstOrDefaultAsync(d => d.Id == docId, ct);
            if (doc == null || string.IsNullOrWhiteSpace(doc.StoragePath)) continue;
            if (!storage.IsConfigured) continue;

            InternalDocPayload payload;
            var localPayload = await localPayloadLoader.TryFromAzureDiExtractionAsync(doc, ct);
            if (localPayload != null)
            {
                payload = localPayload;
            }
            else
            {
                var bytes = await storage.DownloadAsync(doc.StoragePath, ct);
                payload = await internalParse.EnsureParsedAsync(doc, bytes, ct);
            }
            var markdown = payload.Markdown;
            if (payload.Pdf is { Length: > 16 })
            {
                markdown = PdfGroundedMarkdownBuilder.TryBuildResolveMarkdown(markdown, payload.Pdf)
                    ?? markdown;
            }

            var fileName = ResolveUniquePayloadFileName(doc, docId, usedNames);
            usedNames.Add(fileName);
            result.Add(new InternalDocPayload(payload.FileHash, fileName, markdown, payload.Pdf));
        }

        return result;
    }

    private static string ResolveUniquePayloadFileName(
        StoredDocument doc,
        Guid docId,
        IReadOnlySet<string> usedNames)
    {
        var baseName = (doc.Title ?? doc.OriginalFileName ?? "internal-policy").Trim();
        if (baseName.Length == 0) baseName = "internal-policy";
        if (!usedNames.Contains(baseName)) return baseName;

        var suffix = docId.ToString("N")[..8];
        var withSuffix = $"{baseName} ({suffix})";
        return usedNames.Contains(withSuffix) ? $"{baseName} ({docId:N})" : withSuffix;
    }

    private async Task RunReversePhaseAsync(NdAnalysisRun run, CancellationToken ct)
    {
        await EnsureInternalSectionsForRunAsync(run, ct);
        await ClearIntReverseArtifactsAsync(run.Id, ct);

        var sectionRows = await db.NdRegulInternalSections
            .Where(s => s.AnalysisRunId == run.Id)
            .ToListAsync(ct);

        var sections = PointNumberSort.OrderByPointNumber(sectionRows, s => s.SectionRef).ToList();

        var regulatoryClauses = BuildSelectedRegulatoryClauses(run);
        if (regulatoryClauses.Count == 0)
            throw new InvalidOperationException("No selected regulatory clauses available for reverse mapping.");

        var regulatoryByNo = regulatoryClauses
            .GroupBy(c => c.ClauseNo, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().ClauseText, StringComparer.OrdinalIgnoreCase);

        logger.LogInformation(
            "Regul reverse phase started for run {RunId}: {SectionCount} internal section(s), {ClauseCount} regulatory clause(s)",
            run.Id,
            sections.Count,
            regulatoryClauses.Count);

        var intRowsCreated = 0;
        var mappingsCompleted = 0;
        var sectionTotal = sections.Count;

        for (var i = 0; i < sections.Count; i++)
        {
            var section = sections[i];
            var index = i + 1;
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
                reverseRow.Status = "running";
                reverseRow.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);

                logger.LogInformation(
                    "Regul reverse mapping started for run {RunId} section {SectionRef} ({Index}/{Total})",
                    run.Id,
                    section.SectionRef,
                    index,
                    sectionTotal);

                var mapping = await CallReverseMappingAsync(section, regulatoryClauses, ct);
                reverseRow.Status = "completed";
                reverseRow.Mapping = mapping.Mapping;
                reverseRow.MappedClauseNos = JsonSerializer.Serialize(mapping.MappedClauseNos);
                reverseRow.ResultJson = JsonSerializer.Serialize(mapping);
                reverseRow.ErrorMessage = null;
                reverseRow.UpdatedAt = DateTimeOffset.UtcNow;
                mappingsCompleted++;

                var intClauseNo = "";
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
                    intClauseNo = intFinding.ClauseNo;
                    logger.LogInformation(
                        "Regul reverse INT row created for run {RunId} section {SectionRef} clause={IntClause} pointId={PointId}",
                        run.Id,
                        section.SectionRef,
                        intClauseNo,
                        intPoint.Id);
                }

                logger.LogInformation(
                    "Regul reverse mapping completed for run {RunId} section {SectionRef} ({Index}/{Total}) mapping={Mapping} intRow={IntCreated} progress={Completed}/{SectionTotal}",
                    run.Id,
                    section.SectionRef,
                    index,
                    sectionTotal,
                    mapping.Mapping,
                    intClauseNo.Length > 0,
                    mappingsCompleted,
                    sectionTotal);
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Regul reverse mapping failed for run {RunId} section {SectionRef} ({Index}/{Total})",
                    run.Id,
                    section.SectionRef,
                    index,
                    sectionTotal);
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

    private async Task PrepareRegulRunPreForwardAsync(NdAnalysisRun run, CancellationToken ct)
    {
        if (runCancellation.IsStopRequested(run.Id))
            throw new OperationCanceledException();

        if (AnalysisWorkflowEngine.IsRegulPipelineHybrid(run.WorkflowEngine))
        {
            // V5: retrieval reads directly from the already-indexed local-docs pipeline tables
            // (NdLocalDocumentExtractionSection), not from parsed markdown payloads — skip the
            // legacy Landing AI-based parse entirely, it's not needed here and hits a separate
            // paid service this engine has no reason to depend on.
            logger.LogInformation(
                "Regul V5 pre-forward prep for run {RunId}: no legacy parse needed, retrieval reads the local-docs index directly",
                run.Id);
            await EnsureInternalDocsIndexedAsync(run, ct);
            return;
        }

        if (AnalysisWorkflowEngine.IsForwardOnlyFullMarkdown(run.WorkflowEngine))
        {
            // V4 full markdown: clauses are frozen in point snapshots and internal markdown is
            // already parse-cached. Skip library repair/page-refresh here — RefreshPagesAsync
            // re-resolves PDF pages for EVERY point in the regulation doc (storage download per
            // point) and used to stall runs for many minutes before the first LLM call.
            logger.LogInformation(
                "Regul V4 pre-forward prep for run {RunId}: loading cached markdown only (no re-parse, no page refresh)",
                run.Id);
            var internalDocIds = JsonSerializer.Deserialize<List<string>>(run.SelectedInternalDocIds) ?? [];
            await LoadInternalDocPayloadsAsync(internalDocIds, ct);
            return;
        }

        await PrepareLibraryDocumentsForAnalysisAsync(run, ct);
        if (runCancellation.IsStopRequested(run.Id))
            throw new OperationCanceledException();
        await EnsureInternalSectionsForRunAsync(run, ct);
    }

    private async Task PrepareLibraryDocumentsForAnalysisAsync(NdAnalysisRun run, CancellationToken ct)
    {
        var internalDocIds = JsonSerializer.Deserialize<List<string>>(run.SelectedInternalDocIds) ?? [];
        foreach (var idStr in internalDocIds)
        {
            if (runCancellation.IsStopRequested(run.Id))
                throw new OperationCanceledException();
            if (!Guid.TryParse(idStr, out var docId)) continue;
            var doc = await db.StoredDocuments.FirstOrDefaultAsync(d => d.Id == docId, ct);
            if (doc == null) continue;
            await internalSectionService.EnsureSectionsForWorkflowAsync(doc, ct);
        }

        var regDocIds = JsonSerializer.Deserialize<List<string>>(run.SelectedRegulationDocIds) ?? [];
        foreach (var idStr in regDocIds)
        {
            if (!Guid.TryParse(idStr, out var regId)) continue;
            try
            {
                await regulationPointRepair.RecoverMissingPointsAsync(regId, ct);
                await regulationPointRepair.RefreshPagesAsync(regId, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Regulation library prep failed for doc {RegId} on run {RunId}", regId, run.Id);
            }
        }
    }

    private async Task EnsureInternalSectionsForRunAsync(NdAnalysisRun run, CancellationToken ct)
    {
        var count = await db.NdRegulInternalSections.CountAsync(s => s.AnalysisRunId == run.Id, ct);
        if (count > 0)
        {
            await SyncRunSectionsFromLibraryAsync(run, ct);
            return;
        }

        await ExtractAndStoreInternalSectionsAsync(run, ct);
    }

    private async Task SyncRunSectionsFromLibraryAsync(NdAnalysisRun run, CancellationToken ct)
    {
        var internalDocIds = JsonSerializer.Deserialize<List<string>>(run.SelectedInternalDocIds) ?? [];
        var runSections = await db.NdRegulInternalSections
            .Where(s => s.AnalysisRunId == run.Id)
            .ToListAsync(ct);

        foreach (var idStr in internalDocIds)
        {
            if (!Guid.TryParse(idStr, out var docId)) continue;
            var doc = await db.StoredDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == docId, ct);
            if (doc == null) continue;

            var fileName = doc.Title ?? doc.OriginalFileName ?? "policy.pdf";
            var librarySections = await internalSectionService.ListSectionsAsync(docId, ct);
            var byRef = librarySections.ToDictionary(s => s.SectionRef, StringComparer.OrdinalIgnoreCase);
            var runForDoc = runSections
                .Where(s => string.Equals(s.SourceDoc, fileName, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(s.SourceDoc, doc.OriginalFileName, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(s.SourceDoc, doc.Title, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var existingRefs = runForDoc
                .Select(s => s.SectionRef)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var lib in librarySections)
            {
                var match = runForDoc.FirstOrDefault(s =>
                    string.Equals(s.SectionRef, lib.SectionRef, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    match.SectionText = lib.SectionText;
                    match.SourcePage = lib.SourcePage;
                    continue;
                }

                if (existingRefs.Contains(lib.SectionRef)) continue;

                db.NdRegulInternalSections.Add(new NdRegulInternalSection
                {
                    AnalysisRunId = run.Id,
                    SectionRef = lib.SectionRef,
                    SectionText = lib.SectionText,
                    SourceDoc = fileName,
                    SourcePage = lib.SourcePage,
                });
                existingRefs.Add(lib.SectionRef);
            }
        }

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
            return;

        var allSections = new List<NdRegulInternalSection>();

        foreach (var idStr in internalDocIds)
        {
            if (runCancellation.IsStopRequested(run.Id))
                throw new OperationCanceledException();
            if (!Guid.TryParse(idStr, out var docId)) continue;

            var doc = await db.StoredDocuments.FirstOrDefaultAsync(d => d.Id == docId, ct);
            if (doc == null || string.IsNullOrWhiteSpace(doc.StoragePath))
            {
                logger.LogWarning("Internal document {DocId} not found or missing storage path", docId);
                continue;
            }

            var fileName = doc.Title ?? doc.OriginalFileName ?? "policy.pdf";
            var sections = await internalSectionService.EnsureSectionsForWorkflowAsync(doc, ct);

            foreach (var section in sections)
            {
                allSections.Add(new NdRegulInternalSection
                {
                    AnalysisRunId = run.Id,
                    SectionRef = section.SectionRef,
                    SectionText = section.SectionText,
                    SourceDoc = fileName,
                    SourcePage = section.SourcePage,
                });
            }
        }

        if (allSections.Count == 0)
            throw new InvalidOperationException("No internal policy sections extracted for reverse coverage.");

        db.NdRegulInternalSections.AddRange(allSections);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Prepared {Count} internal sections for run {RunId} (library or Landing {Schema})",
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
        var clauses = BuildSelectedRegulatoryClauses(run);
        if (clauses.Count == 0)
            return "No regulatory clauses were selected for this run.";

        return string.Join(
            "\n\n",
            clauses.Select(c => $"REGULATORY CLAUSE {c.ClauseNo}:\n{c.ClauseText}"));
    }

    /// <summary>Regulatory context for reverse/qualitative — only clauses the user selected for this run.</summary>
    private static List<(string ClauseNo, string ClauseText)> BuildSelectedRegulatoryClauses(NdAnalysisRun run)
    {
        var list = new List<(string ClauseNo, string ClauseText)>();
        foreach (var point in run.Points.OrderBy(p => p.CreatedAt))
        {
            var (clauseNo, clauseText) = ParseClauseFromSnapshot(point.PointSnapshot);
            if (string.IsNullOrWhiteSpace(clauseNo) && string.IsNullOrWhiteSpace(clauseText))
                continue;
            list.Add((clauseNo, clauseText));
        }
        return list;
    }

    private async Task FinalizePointCountsAsync(NdAnalysisRun run, CancellationToken ct)
    {
        var points = await db.NdAnalysisPoints
            .Where(p => p.AnalysisRunId == run.Id)
            .ToListAsync(ct);
        // Same reasoning as EnsureForwardFindingsAsync — hybrid engine (V5) points never have a
        // real RegulationPointId, so this must count non-empty-snapshot points for that engine
        // too, or every V5 run's totals/completed counts would read 0 no matter what happened.
        var isHybrid = AnalysisWorkflowEngine.IsRegulPipelineHybrid(run.WorkflowEngine);
        var regulatory = points
            .Where(p => p.RegulationPointId != null || (isHybrid && !string.IsNullOrWhiteSpace(p.PointSnapshot)))
            .ToList();
        var completed = regulatory.Count(p => p.LandingAiStatus == "completed");
        var failed = regulatory.Count(p => p.LandingAiStatus == "failed");
        run.TotalPointsCount = regulatory.Count;
        run.ProcessedPointsCount = completed;
        run.LandingAiCompletedCount = completed;
        run.DualVerifyCompletedCount = completed;
        run.DualVerifyFailedCount = failed;
    }

    private async Task MarkCancelledAsync(NdAnalysisRun run, CancellationToken ct)
    {
        run.Status = "cancelled";
        await NdRegulRunStopHelper.ApplyAsync(db, run, ct);
        run.ProcessedPointsCount = run.Points.Count(p =>
            p.LandingAiStatus is "compliant" or "partial_compliant" or "non_compliant" or "failed" or "cancelled");
        run.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        runCancellation.Clear(run.Id);
    }

    private async Task<NdRegulPolicyContextService.PolicyBundle> ResolveForwardClauseBundleAsync(
        NdAnalysisRun run,
        NdRegulForwardFinding finding,
        CancellationToken ct)
    {
        if (AnalysisWorkflowEngine.IsRegulPipelineHybrid(run.WorkflowEngine))
            return await BuildRetrievalPolicyBundleAsync(finding, ct);

        return await LoadPolicyBundleAsync(run, ct);
    }

    /// <summary>Re-run forward (or full reverse phase) for one point on a Regul workflow run.</summary>
    public async Task ProcessPointAsync(
        Guid runId,
        Guid pointId,
        bool reverseOnly,
        CancellationToken ct)
    {
        var run = await db.NdAnalysisRuns
            .Include(r => r.Points)
            .FirstOrDefaultAsync(r => r.Id == runId, ct)
            ?? throw new InvalidOperationException("Analysis run not found.");

        if (!AnalysisWorkflowEngine.IsRegulFamily(run.WorkflowEngine))
            throw new InvalidOperationException("Not a Regul workflow run.");

        if (await IsDemoOwnedRunAsync(run, ct))
        {
            logger.LogWarning("Refusing live Regul AI point rerun for demo-owned run {RunId}", runId);
            return;
        }

        var point = run.Points.FirstOrDefault(p => p.Id == pointId)
            ?? throw new InvalidOperationException("Analysis point not found.");

        // Same reasoning as EnsureForwardFindingsAsync — hybrid engine (V5) points never have a
        // real RegulationPointId.
        var isHybridPoint = AnalysisWorkflowEngine.IsRegulPipelineHybrid(run.WorkflowEngine);
        if (!point.RegulationPointId.HasValue && !(isHybridPoint && !string.IsNullOrWhiteSpace(point.PointSnapshot)))
            throw new InvalidOperationException("Forward rerun applies to regulatory clauses only, not INT rows.");

        if (runCancellation.IsStopRequested(runId))
        {
            await MarkCancelledAsync(run, ct);
            return;
        }

        run.Status = "running";
        run.RegulPipelineError = null;
        run.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        if (reverseOnly)
        {
            run.RegulPipelinePhase = "reverse";
            await db.SaveChangesAsync(ct);
            await RunReversePhaseAsync(run, ct);
            await FinalizePointCountsAsync(run, ct);
            run.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return;
        }

        // V5 judges from the local-docs index (the clause's own retrieval), so the legacy Landing AI
        // section parse — a separate paid service — must never run here, same as PrepareRegulRunPreForwardAsync.
        if (!isHybridPoint)
            await EnsureInternalSectionsForRunAsync(run, ct);
        await EnsureForwardFindingsAsync(run, ct);
        var finding = await db.NdRegulForwardFindings
            .FirstOrDefaultAsync(f => f.AnalysisRunId == runId && f.AnalysisPointId == pointId, ct);

        point.LandingAiStatus = "pending";
        point.LandingAiResult = null;
        point.LandingAiError = null;
        point.GoogleAiStatus = "pending";
        point.GoogleAiResult = null;
        point.GoogleAiError = null;
        point.DualVerifyStatus = "pending";
        point.FinalStatus = null;
        point.UpdatedAt = DateTimeOffset.UtcNow;

        if (finding != null)
        {
            finding.Status = "pending";
            finding.ErrorMessage = null;
            finding.ResultJson = null;
            finding.UpdatedAt = DateTimeOffset.UtcNow;
        }

        run.RegulPipelinePhase = "forward";
        await db.SaveChangesAsync(ct);

        if (finding == null)
            return;

        ForwardJudgmentPrep? rerunPrep = null;
        try
        {
            if (isHybridPoint)
            {
                // Same parse → extract → index check and Steps 1-6 as a new analysis, for this clause only.
                point.LandingAiStatus = "running";
                run.RegulPipelinePhase = "parsing";
                await db.SaveChangesAsync(ct);
                await EnsureInternalDocsIndexedAsync(run, ct);
                run.RegulPipelinePhase = "retrieval";
                await db.SaveChangesAsync(ct);
                await embeddingRetrieval.RunRetrievalForFindingAsync(run, finding, ct);
                run.RegulPipelinePhase = "forward";
                await db.SaveChangesAsync(ct);
            }

            var clauseBundle = await ResolveForwardClauseBundleAsync(run, finding, ct);
            var cacheContext = clauseBundle.UsesFullMarkdown;
            var prep = rerunPrep = await PrepareForwardJudgmentAsync(finding, point, 1, clauseBundle, cacheContext, run.WorkflowEngine, ct);
            var judgment = await ExecuteForwardJudgmentAsync(prep, ct);
            finding.Status = "completed";
            finding.ErrorMessage = null;
            finding.UpdatedAt = DateTimeOffset.UtcNow;

            var landingMessage = NdRegulJudgmentFormatter.FormatLandingMessage(
                finding.ClauseNo, finding.ClauseText, judgment);
            finding.ResultJson = JsonSerializer.Serialize(judgment);
            NdRegulAnalysisPointSync.ApplyForwardJudgment(point, judgment, landingMessage);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Regul forward rerun failed for clause {ClauseNo}", finding.ClauseNo);
            finding.Status = "failed";
            finding.ErrorMessage = ex.Message;
            finding.UpdatedAt = DateTimeOffset.UtcNow;
            point.LandingAiStatus = "failed";
            point.LandingAiError = ex.Message;
            point.UpdatedAt = DateTimeOffset.UtcNow;
        }

        if (rerunPrep != null)
            db.NdRegulClauseTraces.AddRange(WithSource(rerunPrep.Traces, RegulClauseTraceSources.ClauseRerun));
        await FinalizePointCountsAsync(run, ct);
        var reversePreserved = await db.NdRegulReverseMappings.AnyAsync(m => m.AnalysisRunId == runId, ct);
        if (reversePreserved)
        {
            run.RegulPipelinePhase = "done";
            run.Status = "completed";
        }
        run.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Re-run forward judgments for regulatory clauses only; reverse mappings and INT rows stay in DB.</summary>
    public async Task RerunForwardPhaseAsync(Guid runId, CancellationToken ct)
    {
        var run = await db.NdAnalysisRuns
            .Include(r => r.Points)
            .FirstOrDefaultAsync(r => r.Id == runId, ct)
            ?? throw new InvalidOperationException("Analysis run not found.");

        if (!AnalysisWorkflowEngine.IsRegulFamily(run.WorkflowEngine))
            throw new InvalidOperationException("Not a Regul workflow run.");

        if (await IsDemoOwnedRunAsync(run, ct))
        {
            logger.LogWarning("Refusing live Regul forward rerun for demo-owned run {RunId}", runId);
            return;
        }

        if (runCancellation.IsStopRequested(runId))
        {
            await MarkCancelledAsync(run, ct);
            return;
        }

        // Every clause is judged again, so the run's recorded model must be the one used now.
        var rerunLlm = await llmSettings.GetConfigAsync(ct);
        run.RegulLlmProvider = rerunLlm.Provider;
        run.RegulLlmModel = rerunLlm.Model;
        run.Status = "running";
        run.RegulPipelinePhase = "forward";
        run.RegulPipelineError = null;
        run.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        // Same reasoning as EnsureForwardFindingsAsync — hybrid engine (V5) points never have a
        // real RegulationPointId.
        var isHybridRerun = AnalysisWorkflowEngine.IsRegulPipelineHybrid(run.WorkflowEngine);

        // V5 never uses the legacy Landing AI section parse (see PrepareRegulRunPreForwardAsync).
        if (!isHybridRerun)
            await EnsureInternalSectionsForRunAsync(run, ct);
        var regulatoryPointIds = run.Points
            .Where(p => p.RegulationPointId.HasValue || (isHybridRerun && !string.IsNullOrWhiteSpace(p.PointSnapshot)))
            .Select(p => p.Id)
            .ToHashSet();

        var forwardFindings = await db.NdRegulForwardFindings
            .Where(f => f.AnalysisRunId == runId && f.AnalysisPointId != null)
            .ToListAsync(ct);

        foreach (var finding in forwardFindings)
        {
            if (!finding.AnalysisPointId.HasValue
                || !regulatoryPointIds.Contains(finding.AnalysisPointId.Value))
                continue;

            finding.Status = "pending";
            finding.ResultJson = null;
            finding.ErrorMessage = null;
            finding.UpdatedAt = DateTimeOffset.UtcNow;
        }

        foreach (var point in run.Points.Where(p => regulatoryPointIds.Contains(p.Id)))
        {
            point.LandingAiStatus = "pending";
            point.LandingAiResult = null;
            point.LandingAiError = null;
            point.GoogleAiStatus = "pending";
            point.GoogleAiResult = null;
            point.GoogleAiError = null;
            point.DualVerifyStatus = "pending";
            point.FinalStatus = null;
            point.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Regul forward-only rerun started for run {RunId} ({ClauseCount} regulatory clause(s), reverse preserved)",
            runId,
            regulatoryPointIds.Count);

        try
        {
            if (isHybridRerun)
            {
                // Same parse → extract → index check and Steps 1-6 as a new analysis.
                run.RegulPipelinePhase = "parsing";
                await db.SaveChangesAsync(ct);
                await EnsureInternalDocsIndexedAsync(run, ct);
                run.RegulPipelinePhase = "retrieval";
                run.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                await embeddingRetrieval.RunRetrievalAsync(run, ct);
                run.RegulPipelinePhase = "forward";
                run.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
            }

            await RunForwardPhaseAsync(run, ct, RegulClauseTraceSources.RerunAll);

            run.RegulPipelinePhase = "done";
            run.Status = "completed";
            await FinalizePointCountsAsync(run, ct);
            run.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            logger.LogInformation("Regul forward-only rerun completed for run {RunId} (reverse preserved)", runId);
        }
        catch (OperationCanceledException)
        {
            await MarkCancelledAsync(run, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Regul forward-only rerun failed for run {RunId}", runId);
            run.Status = "failed";
            run.RegulPipelineError = ex.Message;
            run.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    /// <summary>Re-run reverse mapping for all internal sections (Regul workflow only).</summary>
    public async Task RerunReversePhaseAsync(Guid runId, CancellationToken ct)
    {
        var run = await db.NdAnalysisRuns
            .Include(r => r.Points)
            .FirstOrDefaultAsync(r => r.Id == runId, ct)
            ?? throw new InvalidOperationException("Analysis run not found.");

        if (!AnalysisWorkflowEngine.IsRegulPipeline(run.WorkflowEngine))
            throw new InvalidOperationException("Not a Regul workflow run.");

        if (await IsDemoOwnedRunAsync(run, ct))
        {
            logger.LogWarning("Refusing live Regul reverse rerun for demo-owned run {RunId}", runId);
            return;
        }

        if (runCancellation.IsStopRequested(runId))
        {
            await MarkCancelledAsync(run, ct);
            return;
        }

        run.Status = "running";
        run.RegulPipelinePhase = "reverse";
        run.RegulPipelineError = null;
        run.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await RunReversePhaseAsync(run, ct);
        await FinalizePointCountsAsync(run, ct);
        run.RegulPipelinePhase = "done";
        run.Status = "completed";
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

    private async Task<bool> IsDemoOwnedRunAsync(NdAnalysisRun run, CancellationToken ct)
    {
        if (!demoDirectory.IsEnabled || !run.CreatedBy.HasValue) return false;
        var demoIds = await demoDirectory.GetDemoProfileIdsAsync(ct);
        return demoIds.Contains(run.CreatedBy.Value);
    }
}
