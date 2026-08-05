using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Infrastructure.NewDashboard;
using Reguliq.Api.Models;
using Reguliq.Api.Services.LandingAi;
using Reguliq.Api.Services.Storage;

namespace Reguliq.Api.Services.NewDashboard;

public class NdAnalysisProcessor(
    AppDbContext db,
    LandingAiCompareService landingAi,
    NdInternalParseService internalParse,
    DualVerifyLlmService dualVerifyLlm,
    SupabaseStorageService storage,
    IConfiguration configuration,
    NdAnalysisRunCancellationTracker runCancellation,
    ILogger<NdAnalysisProcessor> logger)
{
    private readonly ComparePromptVersion _defaultComparePromptVersion =
        NdAnalysisPromptDefaults.Resolve(configuration);

    private ComparePromptVersion ResolvePromptVersion(NdAnalysisRun run) =>
        ComparePromptVersionExtensions.ParseOrDefault(run.ComparePromptVersion, _defaultComparePromptVersion);

    private static readonly JsonSerializerOptions SnapshotJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static bool IsLandingSuccess(string status) =>
        status is "compliant" or "partial_compliant" or "non_compliant";

    private static bool IsDualDone(string status) =>
        status is "passed" or "failed" or "skipped";

    private static PointSnapshotDto ParsePointSnapshot(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new PointSnapshotDto();
        try
        {
            return JsonSerializer.Deserialize<PointSnapshotDto>(raw, SnapshotJsonOptions)
                   ?? new PointSnapshotDto();
        }
        catch
        {
            return new PointSnapshotDto();
        }
    }

    public async Task ProcessRunAsync(Guid runId, CancellationToken ct)
    {
        var run = await db.NdAnalysisRuns
            .Include(r => r.Points)
            .FirstOrDefaultAsync(r => r.Id == runId, ct)
            ?? throw new InvalidOperationException("Analysis run not found.");

        if (AnalysisWorkflowEngine.IsRegulFamily(run.WorkflowEngine))
            throw new InvalidOperationException(
                "regul_pipeline runs use NdRegulAnalysisProcessor, not NdAnalysisProcessor (dual verify).");

        if (await IsRunStoppedAsync(run, ct))
        {
            await MarkRunCancelledAsync(run, CancellationToken.None);
            return;
        }

        run.Status = "running";
        run.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        // Stop may have won the race while we wrote "running".
        if (await IsRunStoppedAsync(run, CancellationToken.None))
        {
            await MarkRunCancelledAsync(run, CancellationToken.None);
            return;
        }

        var internalDocIds = JsonSerializer.Deserialize<List<string>>(run.SelectedInternalDocIds) ?? [];

        var points = run.Points
            .Where(p =>
                p.LandingAiStatus is "pending" or "failed"
                || (IsLandingSuccess(p.LandingAiStatus) && p.DualVerifyStatus is "pending"))
            .OrderBy(p => p.CreatedAt)
            .ToList();

        foreach (var point in points)
        {
            if (await IsRunStoppedAsync(run, ct))
            {
                await MarkRunCancelledAsync(run, CancellationToken.None);
                return;
            }

            // Re-read status in case Stop endpoint already marked points cancelled.
            await db.Entry(point).ReloadAsync(CancellationToken.None);
            await db.Entry(run).ReloadAsync(CancellationToken.None);
            if (point.LandingAiStatus is "cancelled"
                || point.DualVerifyStatus is "cancelled"
                || run.Status == "cancelled")
            {
                await MarkRunCancelledAsync(run, CancellationToken.None);
                return;
            }

            try
            {
                var internalDocs = await ResolveInternalDocsForPointAsync(point, internalDocIds, ct);
                var dualOnly = IsLandingSuccess(point.LandingAiStatus) && point.DualVerifyStatus is "pending";
                await ProcessPointPipelineAsync(
                    run, point, internalDocs,
                    fullRerun: false, dualVerifyOnly: dualOnly, ct);
            }
            catch (OperationCanceledException)
            {
                await MarkRunCancelledAsync(run, CancellationToken.None);
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Point {PointId} failed on run {RunId}", point.Id, runId);
                point.LandingAiStatus = "failed";
                point.LandingAiError = ex.Message;
                point.DualVerifyStatus = "skipped";
                point.UpdatedAt = DateTimeOffset.UtcNow;
                await UpdateRunCountsAsync(run, CancellationToken.None);
            }
        }

        if (await IsRunStoppedAsync(run, CancellationToken.None))
        {
            await MarkRunCancelledAsync(run, CancellationToken.None);
            return;
        }

        await FinalizeRunStatusAsync(run, CancellationToken.None);
    }

    /// <summary>
    /// Stop can persist cancelled via another DbContext; this processor must not overwrite it on SaveChanges.
    /// </summary>
    private async Task<bool> IsRunStoppedAsync(NdAnalysisRun run, CancellationToken ct)
    {
        if (ct.IsCancellationRequested || runCancellation.IsStopRequested(run.Id))
            return true;
        if (run.Status == "cancelled")
            return true;

        var dbStatus = await db.NdAnalysisRuns.AsNoTracking()
            .Where(r => r.Id == run.Id)
            .Select(r => r.Status)
            .FirstOrDefaultAsync(CancellationToken.None);
        return dbStatus == "cancelled";
    }

    private async Task EnsureNotStoppedAsync(NdAnalysisRun run, CancellationToken ct)
    {
        if (await IsRunStoppedAsync(run, ct))
        {
            await MarkRunCancelledAsync(run, CancellationToken.None);
            throw new OperationCanceledException();
        }
    }

    private async Task MarkRunCancelledAsync(NdAnalysisRun run, CancellationToken ct)
    {
        await db.Entry(run).ReloadAsync(ct);
        await db.Entry(run).Collection(r => r.Points).LoadAsync(ct);

        foreach (var point in run.Points)
        {
            if (point.LandingAiStatus is "pending" or "running")
            {
                point.LandingAiStatus = "cancelled";
                point.LandingAiError ??= "Stopped by user";
                point.DualVerifyStatus = "skipped";
                point.UpdatedAt = DateTimeOffset.UtcNow;
            }
            else if (point.DualVerifyStatus is "pending" or "running")
            {
                point.DualVerifyStatus = "cancelled";
                if (point.GoogleAiStatus is "pending" or "running")
                    point.GoogleAiStatus = "cancelled";
                point.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }

        run.Status = "cancelled";
        run.ProcessedPointsCount = run.Points.Count(p =>
            IsLandingSuccess(p.LandingAiStatus) || p.LandingAiStatus is "failed" or "cancelled");
        run.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Analysis run {RunId} cancelled", run.Id);
    }

    public async Task ProcessPointAsync(
        Guid runId,
        Guid pointId,
        bool dualVerifyOnly,
        bool evidenceOnly = false,
        int? actionIndex = null,
        CancellationToken ct = default)
    {
        var run = await db.NdAnalysisRuns
            .Include(r => r.Points)
            .FirstOrDefaultAsync(r => r.Id == runId, ct)
            ?? throw new InvalidOperationException("Analysis run not found.");

        if (AnalysisWorkflowEngine.IsRegulFamily(run.WorkflowEngine))
            throw new InvalidOperationException(
                "regul_pipeline runs use NdRegulAnalysisProcessor, not NdAnalysisProcessor (dual verify).");

        var point = run.Points.FirstOrDefault(p => p.Id == pointId)
            ?? throw new InvalidOperationException("Analysis point not found.");

        var internalDocIds = JsonSerializer.Deserialize<List<string>>(run.SelectedInternalDocIds) ?? [];
        var internalDocs = evidenceOnly
            ? await ResolveGapEvidenceDocsAsync(point, actionIndex, ct)
            : await ResolveInternalDocsForPointAsync(point, internalDocIds, ct);

        if (dualVerifyOnly)
        {
            point.DualVerifyRerunCount++;
            point.DualVerifyStatus = "pending";
            point.GoogleAiStatus = "running";
            point.GoogleAiError = null;
            await db.SaveChangesAsync(ct);
            await RunDualVerifyOnlyAsync(run, point, internalDocs, ct);
        }
        else
        {
            point.LandingAiRerunCount++;
            point.LandingAiStatus = "running";
            point.DualVerifyStatus = "pending";
            point.GoogleAiStatus = "pending";
            point.LandingAiError = null;
            point.GoogleAiError = null;
            await db.SaveChangesAsync(ct);
            await ProcessPointPipelineAsync(
                run, point, internalDocs,
                fullRerun: true, dualVerifyOnly: false, ct);
        }

        await FinalizeRunStatusAsync(run, ct);
    }

    public async Task RerunAllFailedDualVerifyAsync(Guid runId, CancellationToken ct)
    {
        var run = await db.NdAnalysisRuns
            .Include(r => r.Points)
            .FirstOrDefaultAsync(r => r.Id == runId, ct)
            ?? throw new InvalidOperationException("Analysis run not found.");

        if (AnalysisWorkflowEngine.IsRegulFamily(run.WorkflowEngine))
            throw new InvalidOperationException(
                "regul_pipeline runs use NdRegulAnalysisProcessor.RerunReversePhaseAsync.");

        var internalDocIds = JsonSerializer.Deserialize<List<string>>(run.SelectedInternalDocIds) ?? [];

        foreach (var point in run.Points.Where(p => p.DualVerifyStatus == "failed"))
        {
            var internalDocs = await ResolveInternalDocsForPointAsync(point, internalDocIds, ct);
            point.DualVerifyRerunCount++;
            point.DualVerifyStatus = "pending";
            point.GoogleAiStatus = "running";
            point.GoogleAiError = null;
            await db.SaveChangesAsync(ct);
            await RunDualVerifyOnlyAsync(run, point, internalDocs, ct);
        }

        await FinalizeRunStatusAsync(run, ct);
    }

    private async Task ProcessPointPipelineAsync(
        NdAnalysisRun run,
        NdAnalysisPoint point,
        IReadOnlyList<InternalDocPayload> internalDocs,
        bool fullRerun,
        bool dualVerifyOnly,
        CancellationToken ct)
    {
        await EnsureNotStoppedAsync(run, ct);

        if (dualVerifyOnly)
        {
            await RunDualVerifyOnlyAsync(run, point, internalDocs, ct);
            return;
        }

        var snapshot = ParsePointSnapshot(point.PointSnapshot);
        var govPoint = await NdAnalysisGovPointResolver.ResolveAsync(
            db, point.RegulationPointId, snapshot, ct);
        var promptVersion = ResolvePromptVersion(run);

        if (!IsLandingSuccess(point.LandingAiStatus) || fullRerun)
        {
            point.LandingAiStatus = "running";
            point.UpdatedAt = DateTimeOffset.UtcNow;
            await EnsureNotStoppedAsync(run, ct);
            await db.SaveChangesAsync(ct);

            try
            {
                var forceRefresh = fullRerun;
                var landingMessage = await landingAi.ComparePointAsync(
                    govPoint, internalDocs.ToList(), forceRefresh, promptVersion, ct);

                await EnsureNotStoppedAsync(run, ct);

                point.LandingAiStatus = NdComplianceParser.ExtractStatusFromMessage(landingMessage);
                point.LandingAiResult = JsonSerializer.Serialize(new { message = landingMessage });
                point.LandingAiActionPlan = NdComplianceParser.ExtractActionPlan(landingMessage);
                point.LandingAiRunAt = DateTimeOffset.UtcNow;
                point.LandingAiError = null;

                await SaveInitialActionPlanIfNeededAsync(point, run.CreatedBy, ct);
            }
            catch (OperationCanceledException)
            {
                await MarkRunCancelledAsync(run, CancellationToken.None);
                throw;
            }
            catch (Exception ex)
            {
                point.LandingAiStatus = "failed";
                point.LandingAiError = ex.Message;
                point.DualVerifyStatus = "skipped";
                point.UpdatedAt = DateTimeOffset.UtcNow;
                await UpdateRunCountsAsync(run, ct);
                return;
            }
        }

        await EnsureNotStoppedAsync(run, ct);

        if (!IsLandingSuccess(point.LandingAiStatus))
        {
            point.DualVerifyStatus = "skipped";
            await UpdateRunCountsAsync(run, ct);
            return;
        }

        if (!IsDualDone(point.DualVerifyStatus) || fullRerun)
            await RunDualVerifyPhaseAsync(run, point, govPoint, internalDocs, ct);
        else
            await UpdateRunCountsAsync(run, ct);
    }

    private async Task RunDualVerifyOnlyAsync(
        NdAnalysisRun run,
        NdAnalysisPoint point,
        IReadOnlyList<InternalDocPayload> internalDocs,
        CancellationToken ct)
    {
        var snapshot = ParsePointSnapshot(point.PointSnapshot);
        var govPoint = await NdAnalysisGovPointResolver.ResolveAsync(
            db, point.RegulationPointId, snapshot, ct);

        if (string.IsNullOrWhiteSpace(point.LandingAiResult))
            throw new InvalidOperationException("No Landing AI result to verify against.");

        using var doc = JsonDocument.Parse(point.LandingAiResult);
        var landingMessage = doc.RootElement.TryGetProperty("message", out var m)
            ? m.GetString() ?? ""
            : point.LandingAiResult;

        point.GoogleAiStatus = "running";
        point.DualVerifyStatus = "pending";
        await EnsureNotStoppedAsync(run, ct);
        await db.SaveChangesAsync(ct);

        try
        {
            var phase2 = await RunPhase2Async(govPoint, landingMessage, internalDocs, ResolvePromptVersion(run), ct);
            await EnsureNotStoppedAsync(run, ct);
            ApplyDualVerifyResult(run, point, landingMessage, phase2);
        }
        catch (OperationCanceledException)
        {
            await MarkRunCancelledAsync(run, CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            point.GoogleAiStatus = "failed";
            point.GoogleAiError = ex.Message;
            point.DualVerifyStatus = "failed";
            point.DualVerifyRunAt = DateTimeOffset.UtcNow;
        }

        point.UpdatedAt = DateTimeOffset.UtcNow;
        await UpdateRunCountsAsync(run, ct);
    }

    private async Task RunDualVerifyPhaseAsync(
        NdAnalysisRun run,
        NdAnalysisPoint point,
        GovPoint govPoint,
        IReadOnlyList<InternalDocPayload> internalDocs,
        CancellationToken ct)
    {
        await EnsureNotStoppedAsync(run, ct);

        point.GoogleAiStatus = "running";
        point.DualVerifyStatus = "pending";
        await db.SaveChangesAsync(ct);

        try
        {
            using var doc = JsonDocument.Parse(point.LandingAiResult ?? "{}");
            var landingMessage = doc.RootElement.TryGetProperty("message", out var m)
                ? m.GetString() ?? ""
                : "";

            var phase2 = await RunPhase2Async(govPoint, landingMessage, internalDocs, ResolvePromptVersion(run), ct);
            await EnsureNotStoppedAsync(run, ct);
            ApplyDualVerifyResult(run, point, landingMessage, phase2);
        }
        catch (OperationCanceledException)
        {
            await MarkRunCancelledAsync(run, CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            point.GoogleAiStatus = "failed";
            point.GoogleAiError = ex.Message;
            point.DualVerifyStatus = "failed";
            point.DualVerifyRunAt = DateTimeOffset.UtcNow;
        }

        point.UpdatedAt = DateTimeOffset.UtcNow;
        await UpdateRunCountsAsync(run, ct);
    }

    private async Task<string> RunPhase2Async(
        GovPoint govPoint,
        string landingMessage,
        IReadOnlyList<InternalDocPayload> internalDocs,
        ComparePromptVersion promptVersion,
        CancellationToken ct)
    {
        logger.LogInformation(
            "Phase 2 (independent verifier) for {Point} using prompt {PromptVersion} ({PromptKey}) — fixed template, not admin-editable at runtime (see Admin \u2192 Analysis prompts for reference text only)",
            govPoint.PointId,
            promptVersion,
            promptVersion == ComparePromptVersion.V3 ? "dual_verify_pass2_v3" : promptVersion.ToString());

        var markdownSupplement = BuildInternalMarkdownSupplement(internalDocs);
        var attachedNames = internalDocs.Select(d => d.FileName).ToList();
        var prompt = DualVerifyPromptBuilder.Build(
            govPoint, landingMessage, markdownSupplement, attachedNames, promptVersion);

        var pdfs = internalDocs
            .Where(d => d.Pdf is { Length: > 0 } && LandingAiDocumentFormats.IsPdf(d.FileName, d.Pdf))
            .Select(d => (d.Pdf!, d.FileName))
            .ToList();

        var skippedNonPdf = internalDocs.Count(d => d.Pdf is { Length: > 0 }) - pdfs.Count;
        if (skippedNonPdf > 0)
        {
            logger.LogInformation(
                "Phase 2: skipping {Count} non-PDF internal file(s); using parsed markdown in prompt",
                skippedNonPdf);
        }

        // Prefer text when we have parsed markdown (Word uploads, cached parse, etc.) — same as legacy dual-verify worker.
        var hasMarkdown = !string.IsNullOrWhiteSpace(markdownSupplement) && markdownSupplement.Trim().Length > 100;
        if (hasMarkdown || pdfs.Count == 0)
            return await dualVerifyLlm.AnalyzeTextAsync(prompt, ct);

        return await dualVerifyLlm.AnalyzeWithPdfsAsync(pdfs, prompt, ct);
    }

    private static string BuildInternalMarkdownSupplement(IReadOnlyList<InternalDocPayload> internalDocs)
    {
        if (internalDocs.Count == 0) return "";
        if (internalDocs.Count == 1)
            return internalDocs[0].Markdown;

        return string.Join(
            "\n\n",
            internalDocs.Select((d, i) => $"--- INTERNAL DOCUMENT {i + 1}: {d.FileName} ---\n{d.Markdown}"));
    }

    private void ApplyDualVerifyResult(NdAnalysisRun run, NdAnalysisPoint point, string landingMessage, string phase2)
    {
        var agreement = NdComplianceParser.ComparePasses(landingMessage, phase2);
        point.GoogleAiStatus = NdComplianceParser.NormalizeStatus(agreement.LlmStatus);
        point.GoogleAiResult = JsonSerializer.Serialize(new { message = phase2, agreement });
        point.GoogleAiRunAt = DateTimeOffset.UtcNow;
        point.GoogleAiError = null;
        point.DualVerifyRunAt = DateTimeOffset.UtcNow;
        point.DualVerifyStatus = agreement.Status == "aligned" ? "passed" : "failed";
        point.FinalStatus = ResolveFinalStatusFromAgreement(agreement);
    }

    private static string ResolveFinalStatusFromAgreement(DualVerifyAgreementDto agreement)
    {
        if (agreement.Status == "both_non_compliant")
            return "non_compliant";
        if (agreement.Status is "status_mismatch" or "confidence_gap")
            return "partial_compliant";
        return NdComplianceParser.NormalizeStatus(agreement.LandingStatus);
    }

    private async Task SaveInitialActionPlanIfNeededAsync(
        NdAnalysisPoint point,
        Guid? createdBy,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(point.OriginalAiActionPlan))
            return;

        var plan = point.LandingAiActionPlan;
        if (string.IsNullOrWhiteSpace(plan))
            return;

        point.OriginalAiActionPlan = plan;
        point.FinalActionPlan = plan;

        var hasHistory = await db.NdActionPlanHistories
            .AnyAsync(h => h.AnalysisPointId == point.Id, ct);
        if (hasHistory) return;

        db.NdActionPlanHistories.Add(new NdActionPlanHistory
        {
            AnalysisPointId = point.Id,
            ActionPlanContent = plan,
            VersionNumber = 1,
            ChangeType = "ai_original",
            ChangedBy = createdBy,
            IsCurrent = true,
        });
        await db.SaveChangesAsync(ct);
    }

    private async Task UpdateRunCountsAsync(NdAnalysisRun run, CancellationToken ct)
    {
        if (await IsRunStoppedAsync(run, ct))
        {
            await MarkRunCancelledAsync(run, CancellationToken.None);
            throw new OperationCanceledException();
        }

        run.ProcessedPointsCount = run.Points.Count(p =>
            IsLandingSuccess(p.LandingAiStatus) || p.LandingAiStatus == "failed");
        run.LandingAiCompletedCount = run.Points.Count(p => IsLandingSuccess(p.LandingAiStatus));
        run.DualVerifyCompletedCount = run.Points.Count(p => p.DualVerifyStatus == "passed");
        run.DualVerifyFailedCount = run.Points.Count(p => p.DualVerifyStatus == "failed");
        run.UpdatedAt = DateTimeOffset.UtcNow;

        // Never let a stale tracked Status="running" overwrite Stop's cancelled row.
        var dbStatus = await db.NdAnalysisRuns.AsNoTracking()
            .Where(r => r.Id == run.Id)
            .Select(r => r.Status)
            .FirstOrDefaultAsync(CancellationToken.None);
        if (dbStatus == "cancelled")
        {
            await MarkRunCancelledAsync(run, CancellationToken.None);
            throw new OperationCanceledException();
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task FinalizeRunStatusAsync(NdAnalysisRun run, CancellationToken ct)
    {
        if (await IsRunStoppedAsync(run, ct))
        {
            await MarkRunCancelledAsync(run, CancellationToken.None);
            return;
        }

        await UpdateRunCountsAsync(run, ct);

        var total = run.TotalPointsCount;
        var landingDone = run.Points.Count(p =>
            IsLandingSuccess(p.LandingAiStatus) || p.LandingAiStatus == "failed");

        if (landingDone >= total)
        {
            run.Status = run.DualVerifyFailedCount > 0 ? "dual_verify_failed" : "completed";
        }
        else if (run.LandingAiCompletedCount > 0)
        {
            run.Status = "landing_ai_complete";
        }

        run.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private async Task<List<InternalDocPayload>> ResolveInternalDocsForPointAsync(
        NdAnalysisPoint point,
        List<string> runInternalDocIds,
        CancellationToken ct)
    {
        var attachmentIds = await db.NdAnalysisPointAttachments.AsNoTracking()
            .Where(a => a.AnalysisPointId == point.Id)
            .Select(a => a.StoredDocumentId.ToString())
            .ToListAsync(ct);

        var merged = runInternalDocIds
            .Concat(attachmentIds)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return await ResolveInternalDocsAsync(merged, ct);
    }

    private async Task<List<InternalDocPayload>> ResolveGapEvidenceDocsAsync(
        NdAnalysisPoint point,
        int? actionIndex,
        CancellationToken ct)
    {
        var query = db.NdAnalysisPointAttachments.AsNoTracking()
            .Where(a => a.AnalysisPointId == point.Id);

        if (actionIndex.HasValue)
            query = query.Where(a => a.ActionIndex == actionIndex.Value);

        var attachmentIds = await query
            .Select(a => a.StoredDocumentId.ToString())
            .ToListAsync(ct);

        if (attachmentIds.Count == 0)
            throw new InvalidOperationException(
                actionIndex.HasValue
                    ? "No gap evidence documents uploaded for this action item."
                    : "No gap evidence documents uploaded for this point.");

        return await ResolveInternalDocsAsync(attachmentIds, ct);
    }

    private async Task<List<InternalDocPayload>> ResolveInternalDocsAsync(
        List<string> internalDocIds,
        CancellationToken ct)
    {
        var result = new List<InternalDocPayload>();
        if (internalDocIds.Count == 0)
            throw new InvalidOperationException("No internal or gap-evidence documents available for this point.");

        foreach (var idStr in internalDocIds)
        {
            if (!Guid.TryParse(idStr, out var docId))
                continue;

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
            result.Add(payload);
        }

        if (result.Count == 0)
            throw new InvalidOperationException("No internal documents could be loaded for this run.");

        return result;
    }
}

public class PointSnapshotDto
{
    public string? PointId { get; set; }
    public string? PointNumber { get; set; }
    public string? PointTitle { get; set; }
    public string? PointContent { get; set; }
    public string? PageReference { get; set; }
    public Guid? RegulationDocumentId { get; set; }
    public Guid? RegulationPointId { get; set; }
}
