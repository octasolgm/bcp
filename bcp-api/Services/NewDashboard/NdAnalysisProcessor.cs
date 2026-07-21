using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Models;
using Reguliq.Api.Services.LandingAi;
using Reguliq.Api.Services.Storage;

namespace Reguliq.Api.Services.NewDashboard;

public class NdAnalysisProcessor(
    AppDbContext db,
    LandingAiCompareService landingAi,
    NdInternalParseService internalParse,
    GeminiService gemini,
    SupabaseStorageService storage,
    IConfiguration config,
    ILogger<NdAnalysisProcessor> logger)
{
    private static bool IsLandingSuccess(string status) =>
        status is "compliant" or "partial_compliant" or "non_compliant";

    private static bool IsDualDone(string status) =>
        status is "passed" or "failed" or "skipped";

    public async Task ProcessRunAsync(Guid runId, CancellationToken ct)
    {
        var run = await db.NdAnalysisRuns
            .Include(r => r.Points)
            .FirstOrDefaultAsync(r => r.Id == runId, ct)
            ?? throw new InvalidOperationException("Analysis run not found.");

        run.Status = "running";
        run.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        var internalDocIds = JsonSerializer.Deserialize<List<string>>(run.SelectedInternalDocIds) ?? [];
        var phase2Model = ResolvePhase2Model();

        var points = run.Points
            .Where(p =>
                p.LandingAiStatus is "pending" or "failed"
                || (IsLandingSuccess(p.LandingAiStatus) && p.DualVerifyStatus is "pending"))
            .OrderBy(p => p.CreatedAt)
            .ToList();

        foreach (var point in points)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var internalDocs = await ResolveInternalDocsForPointAsync(point, internalDocIds, ct);
                var dualOnly = IsLandingSuccess(point.LandingAiStatus) && point.DualVerifyStatus is "pending";
                await ProcessPointPipelineAsync(
                    run, point, internalDocs, phase2Model,
                    fullRerun: false, dualVerifyOnly: dualOnly, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Point {PointId} failed on run {RunId}", point.Id, runId);
                point.LandingAiStatus = "failed";
                point.LandingAiError = ex.Message;
                point.DualVerifyStatus = "skipped";
                point.UpdatedAt = DateTimeOffset.UtcNow;
                await UpdateRunCountsAsync(run, ct);
            }
        }

        await FinalizeRunStatusAsync(run, ct);
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

        var point = run.Points.FirstOrDefault(p => p.Id == pointId)
            ?? throw new InvalidOperationException("Analysis point not found.");

        var internalDocIds = JsonSerializer.Deserialize<List<string>>(run.SelectedInternalDocIds) ?? [];
        var internalDocs = evidenceOnly
            ? await ResolveGapEvidenceDocsAsync(point, actionIndex, ct)
            : await ResolveInternalDocsForPointAsync(point, internalDocIds, ct);
        var phase2Model = ResolvePhase2Model();

        if (dualVerifyOnly)
        {
            point.DualVerifyRerunCount++;
            point.DualVerifyStatus = "pending";
            point.GoogleAiStatus = "pending";
            await db.SaveChangesAsync(ct);
            await RunDualVerifyOnlyAsync(run, point, internalDocs, phase2Model, ct);
        }
        else
        {
            point.LandingAiRerunCount++;
            point.LandingAiStatus = "pending";
            point.DualVerifyStatus = "pending";
            point.GoogleAiStatus = "pending";
            point.LandingAiError = null;
            point.GoogleAiError = null;
            await db.SaveChangesAsync(ct);
            await ProcessPointPipelineAsync(
                run, point, internalDocs, phase2Model,
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

        var internalDocIds = JsonSerializer.Deserialize<List<string>>(run.SelectedInternalDocIds) ?? [];
        var phase2Model = ResolvePhase2Model();

        foreach (var point in run.Points.Where(p => p.DualVerifyStatus == "failed"))
        {
            var internalDocs = await ResolveInternalDocsForPointAsync(point, internalDocIds, ct);
            point.DualVerifyRerunCount++;
            point.DualVerifyStatus = "pending";
            point.GoogleAiStatus = "pending";
            await db.SaveChangesAsync(ct);
            await RunDualVerifyOnlyAsync(run, point, internalDocs, phase2Model, ct);
        }

        await FinalizeRunStatusAsync(run, ct);
    }

    private async Task ProcessPointPipelineAsync(
        NdAnalysisRun run,
        NdAnalysisPoint point,
        IReadOnlyList<InternalDocPayload> internalDocs,
        string phase2Model,
        bool fullRerun,
        bool dualVerifyOnly,
        CancellationToken ct)
    {
        if (dualVerifyOnly)
        {
            await RunDualVerifyOnlyAsync(run, point, internalDocs, phase2Model, ct);
            return;
        }

        var snapshot = JsonSerializer.Deserialize<PointSnapshotDto>(point.PointSnapshot)
            ?? new PointSnapshotDto();
        var govPoint = new GovPoint(
            snapshot.PointNumber ?? snapshot.PointId ?? "",
            snapshot.PointTitle,
            snapshot.PointContent ?? "",
            snapshot.PageReference);

        if (!IsLandingSuccess(point.LandingAiStatus) || fullRerun)
        {
            point.LandingAiStatus = "running";
            point.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            try
            {
                var forceRefresh = fullRerun && point.LandingAiRerunCount > 0;
                var landingMessage = await landingAi.ComparePointAsync(
                    govPoint, internalDocs.ToList(), forceRefresh, ct);

                point.LandingAiStatus = NdComplianceParser.ExtractStatusFromMessage(landingMessage);
                point.LandingAiResult = JsonSerializer.Serialize(new { message = landingMessage });
                point.LandingAiActionPlan = NdComplianceParser.ExtractActionPlan(landingMessage);
                point.LandingAiRunAt = DateTimeOffset.UtcNow;
                point.LandingAiError = null;

                await SaveInitialActionPlanIfNeededAsync(point, run.CreatedBy, ct);
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

        if (!IsLandingSuccess(point.LandingAiStatus))
        {
            point.DualVerifyStatus = "skipped";
            await UpdateRunCountsAsync(run, ct);
            return;
        }

        if (!IsDualDone(point.DualVerifyStatus) || fullRerun)
            await RunDualVerifyPhaseAsync(run, point, govPoint, internalDocs, phase2Model, ct);
        else
            await UpdateRunCountsAsync(run, ct);
    }

    private async Task RunDualVerifyOnlyAsync(
        NdAnalysisRun run,
        NdAnalysisPoint point,
        IReadOnlyList<InternalDocPayload> internalDocs,
        string phase2Model,
        CancellationToken ct)
    {
        var snapshot = JsonSerializer.Deserialize<PointSnapshotDto>(point.PointSnapshot)
            ?? new PointSnapshotDto();
        var govPoint = new GovPoint(
            snapshot.PointNumber ?? snapshot.PointId ?? "",
            snapshot.PointTitle,
            snapshot.PointContent ?? "",
            snapshot.PageReference);

        if (string.IsNullOrWhiteSpace(point.LandingAiResult))
            throw new InvalidOperationException("No Landing AI result to verify against.");

        using var doc = JsonDocument.Parse(point.LandingAiResult);
        var landingMessage = doc.RootElement.TryGetProperty("message", out var m)
            ? m.GetString() ?? ""
            : point.LandingAiResult;

        point.GoogleAiStatus = "running";
        point.DualVerifyStatus = "pending";
        await db.SaveChangesAsync(ct);

        try
        {
            var phase2 = await RunGeminiPhase2Async(govPoint, landingMessage, internalDocs, phase2Model, ct);
            ApplyDualVerifyResult(run, point, landingMessage, phase2);
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
        string phase2Model,
        CancellationToken ct)
    {
        point.GoogleAiStatus = "running";
        point.DualVerifyStatus = "pending";
        await db.SaveChangesAsync(ct);

        try
        {
            using var doc = JsonDocument.Parse(point.LandingAiResult ?? "{}");
            var landingMessage = doc.RootElement.TryGetProperty("message", out var m)
                ? m.GetString() ?? ""
                : "";

            var phase2 = await RunGeminiPhase2Async(govPoint, landingMessage, internalDocs, phase2Model, ct);
            ApplyDualVerifyResult(run, point, landingMessage, phase2);
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

    private async Task<string> RunGeminiPhase2Async(
        GovPoint govPoint,
        string landingMessage,
        IReadOnlyList<InternalDocPayload> internalDocs,
        string phase2Model,
        CancellationToken ct)
    {
        var markdownSupplement = BuildInternalMarkdownSupplement(internalDocs);
        var attachedNames = internalDocs.Select(d => d.FileName).ToList();
        var prompt = DualVerifyPromptBuilder.Build(
            govPoint, landingMessage, markdownSupplement, attachedNames);

        var pdfs = internalDocs
            .Where(d => d.Pdf is { Length: > 0 })
            .Select(d => (d.Pdf!, d.FileName))
            .ToList();

        if (pdfs.Count > 0)
            return await gemini.AnalyzeWithPdfsAsync(pdfs, prompt, phase2Model, ct);

        return await gemini.AnalyzeTextAsync(prompt, phase2Model, ct);
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
        run.ProcessedPointsCount = run.Points.Count(p =>
            IsLandingSuccess(p.LandingAiStatus) || p.LandingAiStatus == "failed");
        run.LandingAiCompletedCount = run.Points.Count(p => IsLandingSuccess(p.LandingAiStatus));
        run.DualVerifyCompletedCount = run.Points.Count(p => p.DualVerifyStatus == "passed");
        run.DualVerifyFailedCount = run.Points.Count(p => p.DualVerifyStatus == "failed");
        run.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private async Task FinalizeRunStatusAsync(NdAnalysisRun run, CancellationToken ct)
    {
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

    private string ResolvePhase2Model()
    {
        var model = config["Gemini:DefaultModel"];
        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException("Gemini:DefaultModel is not configured.");
        return model;
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
