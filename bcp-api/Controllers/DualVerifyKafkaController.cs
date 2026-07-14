using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Models;
using Reguliq.Api.Services;

namespace Reguliq.Api.Controllers;

[ApiController]
[Route("dual-verify-kafka")]
public class DualVerifyKafkaController(
    DualVerifyService service,
    DualVerifyStoreService store,
    AppDbContext db,
    ILogger<DualVerifyKafkaController> logger) : ControllerBase
{
    [HttpGet("health")]
    public async Task<ActionResult<ApiResponse<DualVerifyHealthDto>>> Health(CancellationToken ct) =>
        Ok(new ApiResponse<DualVerifyHealthDto>(true, await service.GetHealthAsync(ct)));

    [HttpGet("sessions")]
    public async Task<ActionResult<ApiResponse<object>>> ListSessions(CancellationToken ct)
    {
        var sessions = await store.ListRecentAsync(30, ct);
        var data = sessions.Select(s => new
        {
            s.Id,
            s.Status,
            s.Granularity,
            s.TotalPoints,
            s.CompletedPoints,
            s.FailedPoints,
            phase2Model = s.Phase2Model,
            transport = s.Transport,
            updatedAt = s.UpdatedAt.ToString("o"),
            label = $"{s.Granularity} · {s.CompletedPoints}/{s.TotalPoints} done · {s.UpdatedAt:yyyy-MM-dd HH:mm}"
        });
        return Ok(new ApiResponse<object>(true, data));
    }

    [HttpGet("sessions/active")]
    public async Task<ActionResult<ApiResponse<object>>> ListActiveSessions(CancellationToken ct)
    {
        var sessions = await store.ListActiveAsync(ct);
        var sessionIds = sessions.Select(s => s.Id).ToList();
        var runs = sessionIds.Count == 0
            ? new Dictionary<Guid, Data.Entities.DocumentAnalysisRun>()
            : await db.DocumentAnalysisRuns.AsNoTracking()
                .Where(r => r.DualVerifySessionId != null && sessionIds.Contains(r.DualVerifySessionId.Value))
                .ToDictionaryAsync(r => r.DualVerifySessionId!.Value, r => r, ct);

        var data = sessions.Select(s =>
        {
            runs.TryGetValue(s.Id, out var run);
            var regName = run?.RegulationFileName ?? s.GovFileName;
            var intName = run?.InternalFileName ?? s.InternalFileName;
            var fileLabel = !string.IsNullOrWhiteSpace(regName)
                ? $"{regName} × {intName ?? "compliance"}"
                : null;
            return new
            {
                s.Id,
                s.Status,
                s.Granularity,
                s.TotalPoints,
                s.CompletedPoints,
                s.FailedPoints,
                runningPoints = s.RunningPoints,
                phase2Model = s.Phase2Model,
                transport = s.Transport,
                updatedAt = s.UpdatedAt.ToString("o"),
                regulationFileName = regName,
                internalFileName = intName,
                govFileName = s.GovFileName,
                label = fileLabel ?? $"{s.CompletedPoints}/{s.TotalPoints} pts",
            };
        });
        return Ok(new ApiResponse<object>(true, data));
    }

    [HttpPost("jobs")]
    [RequestSizeLimit(50_000_000)]
    public async Task<ActionResult<ApiResponse<object>>> CreateJob(CancellationToken ct)
    {
        try
        {
            var form = await Request.ReadFormAsync(ct);
            var pointIdsJson = form["pointIds"].FirstOrDefault() ?? "[]";
            var pointIds = JsonSerializer.Deserialize<List<string>>(pointIdsJson) ?? [];
            var request = new CreateDualVerifyJobRequest(
                pointIds,
                form["granularity"].FirstOrDefault() ?? "leaf",
                form["govDocId"].FirstOrDefault() ?? "gov-tfs-guidelines",
                form["internalDocId"].FirstOrDefault() ?? "internal-imptfs",
                form["phase2Model"].FirstOrDefault() ?? "gemini-3.5-flash",
                bool.TryParse(form["forceRefresh"].FirstOrDefault(), out var fr) && fr);

            byte[]? pdf = null;
            var file = form.Files.GetFile("internalFile");
            var fileName = file?.FileName;
            if (file != null)
            {
                using var ms = new MemoryStream();
                await file.CopyToAsync(ms, ct);
                pdf = ms.ToArray();
            }

            IReadOnlyList<GovPoint>? clientGovPoints = null;
            var govPointsJson = form["govPointsJson"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(govPointsJson))
            {
                clientGovPoints = JsonSerializer.Deserialize<List<ClientGovPointDto>>(
                    govPointsJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })?
                    .Select(p => new GovPoint(p.PointId, p.Title, p.Text, p.Section))
                    .ToList();
            }

            var session = await service.CreateJobAsync(
                request,
                pdf,
                fileName,
                clientGovPoints,
                Guid.TryParse(form["analysisInternalDocumentId"].FirstOrDefault(), out var storedInternalId)
                    ? storedInternalId
                    : null,
                ct);

            // Optional Analyse metadata — does not change Kafka job fields/behavior.
            Guid? analysisRunId = null;
            try
            {
                analysisRunId = await TryRecordAnalysisRunAsync(form, session, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not record document analysis run for session {Session}", session.Id);
            }

            return Ok(new ApiResponse<object>(true, new
            {
                id = session.Id,
                analysisRunId,
            }));
        }
        catch (Exception ex)
        {
            return BadRequest(new ApiResponse<object>(false, null!, ex.Message));
        }
    }

    private async Task<Guid?> TryRecordAnalysisRunAsync(
        IFormCollection form,
        Data.Entities.DualVerifySession session,
        CancellationToken ct)
    {
        var internalDocIdRaw = form["analysisInternalDocumentId"].FirstOrDefault();
        var regulationDocIdRaw = form["analysisRegulationDocumentId"].FirstOrDefault();
        var workspaceId = form["analysisWorkspaceId"].FirstOrDefault() ?? "snb-uae-difc";
        var regFileName = form["analysisRegulationFileName"].FirstOrDefault()
            ?? session.GovFileName
            ?? "regulation";
        var internalFileName = form["analysisInternalFileName"].FirstOrDefault()
            ?? session.InternalFileName
            ?? "compliance";

        Guid? internalDocId = Guid.TryParse(internalDocIdRaw, out var iid) ? iid : null;
        Guid? regulationDocId = Guid.TryParse(regulationDocIdRaw, out var rid) ? rid : null;

        // Resolve by stored doc id when provided; otherwise try match by internal file name title.
        if (internalDocId == null && !string.IsNullOrWhiteSpace(internalFileName))
        {
            var title = Path.GetFileNameWithoutExtension(internalFileName).Trim();
            var match = await db.StoredDocuments
                .AsNoTracking()
                .Where(d => d.DocKind == "document" && d.WorkspaceId == workspaceId)
                .OrderByDescending(d => d.UpdatedAt)
                .FirstOrDefaultAsync(
                    d => d.OriginalFileName == internalFileName || d.Title.ToLower() == title.ToLower(),
                    ct);
            if (match != null) internalDocId = match.Id;
        }

        if (regulationDocId == null && !string.IsNullOrWhiteSpace(regFileName))
        {
            var title = Path.GetFileNameWithoutExtension(regFileName).Trim();
            var match = await db.StoredDocuments
                .AsNoTracking()
                .Where(d => d.DocKind == "regulation" && d.WorkspaceId == workspaceId)
                .OrderByDescending(d => d.UpdatedAt)
                .FirstOrDefaultAsync(
                    d => d.OriginalFileName == regFileName || d.Title.ToLower() == title.ToLower(),
                    ct);
            if (match != null) regulationDocId = match.Id;
        }

        var label = $"{regFileName} × {internalFileName} · {session.TotalPoints} pts · {session.Granularity}";
        var run = new Data.Entities.DocumentAnalysisRun
        {
            WorkspaceId = workspaceId,
            InternalDocumentId = internalDocId,
            RegulationDocumentId = regulationDocId,
            DualVerifySessionId = session.Id,
            Label = label,
            RegulationFileName = regFileName,
            InternalFileName = internalFileName,
            InternalFileHash = session.InternalFileHash,
            GovFileHash = session.GovFileHash,
            Status = session.Status,
            PointCount = session.TotalPoints,
            CompletedPoints = session.CompletedPoints,
            Granularity = session.Granularity,
        };
        db.DocumentAnalysisRuns.Add(run);

        if (internalDocId is Guid docId)
        {
            var doc = await db.StoredDocuments.FirstOrDefaultAsync(d => d.Id == docId, ct);
            if (doc != null)
            {
                doc.Status = "gaps";
                doc.GapCount = session.TotalPoints;
                doc.UpdatedAt = DateTimeOffset.UtcNow;
                try
                {
                    var history = System.Text.Json.JsonSerializer.Deserialize<List<string>>(doc.HistoryJson) ?? [];
                    history.Insert(0, $"Analysis {DateTime.UtcNow:u} · {label}");
                    doc.HistoryJson = System.Text.Json.JsonSerializer.Serialize(history.Take(40).ToList());
                }
                catch { /* ignore history parse */ }
            }
        }

        await db.SaveChangesAsync(ct);
        return run.Id;
    }

    [HttpPost("jobs/json")]
    public async Task<ActionResult<ApiResponse<object>>> CreateJobJson(
        [FromBody] CreateDualVerifyJobRequest request, CancellationToken ct)
    {
        try
        {
            var session = await service.CreateJobAsync(request, null, null, null, null, ct);
            return Ok(new ApiResponse<object>(true, new { id = session.Id }));
        }
        catch (Exception ex)
        {
            return BadRequest(new ApiResponse<object>(false, null!, ex.Message));
        }
    }

    [HttpGet("jobs/{sessionId:guid}")]
    public async Task<ActionResult<ApiResponse<SessionProgressDto>>> GetJob(Guid sessionId, CancellationToken ct)
    {
        var progress = await service.GetProgressAsync(sessionId, ct);
        if (progress == null)
            return NotFound(new ApiResponse<SessionProgressDto>(false, null!, "Session not found"));
        return Ok(new ApiResponse<SessionProgressDto>(true, progress));
    }

    [HttpGet("jobs/{sessionId:guid}/results")]
    public async Task<ActionResult<ApiResponse<List<PointJobDto>>>> GetResults(Guid sessionId, CancellationToken ct)
    {
        var results = await service.GetResultsAsync(sessionId, ct);
        if (results.Count == 0)
        {
            var progress = await service.GetProgressAsync(sessionId, ct);
            if (progress == null)
                return NotFound(new ApiResponse<List<PointJobDto>>(false, null!, "Session not found"));
        }
        return Ok(new ApiResponse<List<PointJobDto>>(true, results));
    }

    [HttpPost("jobs/{sessionId:guid}/retry-failed")]
    [RequestSizeLimit(50_000_000)]
    public async Task<ActionResult<ApiResponse<object>>> RetryFailed(Guid sessionId, CancellationToken ct)
    {
        byte[]? pdf = null;
        if (Request.HasFormContentType)
        {
            var form = await Request.ReadFormAsync(ct);
            var file = form.Files.GetFile("internalFile");
            if (file != null)
            {
                using var ms = new MemoryStream();
                await file.CopyToAsync(ms, ct);
                pdf = ms.ToArray();
            }
        }

        var count = await service.RetryFailedAsync(sessionId, pdf, ct);
        return Ok(new ApiResponse<object>(true, new { requeued = count }));
    }

    /// <summary>
    /// Re-run specific points (or append not-yet-run points) on an existing session.
    /// Form fields: pointIds (JSON array), optional govPointsJson, forceRefresh, internalFile.
    /// </summary>
    [HttpPost("jobs/{sessionId:guid}/retry-points")]
    [RequestSizeLimit(50_000_000)]
    public async Task<ActionResult<ApiResponse<object>>> RetryPoints(Guid sessionId, CancellationToken ct)
    {
        try
        {
            if (!Request.HasFormContentType)
                return BadRequest(new ApiResponse<object>(false, null!, "multipart/form-data required"));

            var form = await Request.ReadFormAsync(ct);
            var pointIdsJson = form["pointIds"].FirstOrDefault() ?? "[]";
            var pointIds = JsonSerializer.Deserialize<List<string>>(
                pointIdsJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];

            List<GovPoint>? clientGovPoints = null;
            var govPointsJson = form["govPointsJson"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(govPointsJson))
            {
                clientGovPoints = JsonSerializer.Deserialize<List<ClientGovPointDto>>(
                    govPointsJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })?
                    .Select(p => new GovPoint(p.PointId, p.Title, p.Text, p.Section))
                    .ToList();
            }

            var forceRefresh = string.Equals(
                form["forceRefresh"].FirstOrDefault(), "true", StringComparison.OrdinalIgnoreCase);

            byte[]? pdf = null;
            var file = form.Files.GetFile("internalFile");
            if (file != null)
            {
                using var ms = new MemoryStream();
                await file.CopyToAsync(ms, ct);
                pdf = ms.ToArray();
            }

            var count = await service.RetryPointsAsync(
                sessionId, pointIds, clientGovPoints, pdf, forceRefresh, ct);
            return Ok(new ApiResponse<object>(true, new { requeued = count }));
        }
        catch (Exception ex)
        {
            return BadRequest(new ApiResponse<object>(false, null!, ex.Message));
        }
    }

    [HttpPost("jobs/{sessionId:guid}/cancel")]
    public async Task<ActionResult<ApiResponse<object>>> CancelJob(Guid sessionId, CancellationToken ct)
    {
        var ok = await service.CancelSessionAsync(sessionId, ct);
        if (!ok)
            return NotFound(new ApiResponse<object>(false, null!, "Session not found"));
        return Ok(new ApiResponse<object>(true, new { cancelled = true }));
    }

    [HttpDelete("jobs/{sessionId:guid}")]
    public async Task<ActionResult<ApiResponse<object>>> DeleteJob(Guid sessionId, CancellationToken ct)
    {
        var ok = await service.DeleteSessionAsync(sessionId, ct);
        if (!ok)
            return NotFound(new ApiResponse<object>(false, null!, "Session not found"));
        return Ok(new ApiResponse<object>(true, new { deleted = true }));
    }
}
