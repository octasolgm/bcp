using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Models;
using Reguliq.Api.Services;

namespace Reguliq.Api.Controllers;

[ApiController]
[Route("dual-verify-kafka")]
public class DualVerifyKafkaController(DualVerifyService service, DualVerifyStoreService store) : ControllerBase
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
                form["phase2Model"].FirstOrDefault() ?? "gemini-2.5-flash-lite",
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

            var session = await service.CreateJobAsync(request, pdf, fileName, ct);
            return Ok(new ApiResponse<object>(true, session));
        }
        catch (Exception ex)
        {
            return BadRequest(new ApiResponse<object>(false, null!, ex.Message));
        }
    }

    [HttpPost("jobs/json")]
    public async Task<ActionResult<ApiResponse<object>>> CreateJobJson(
        [FromBody] CreateDualVerifyJobRequest request, CancellationToken ct)
    {
        try
        {
            var session = await service.CreateJobAsync(request, null, null, ct);
            return Ok(new ApiResponse<object>(true, session));
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

    [HttpPost("jobs/{sessionId:guid}/retry-failed")]
    public async Task<ActionResult<ApiResponse<object>>> RetryFailed(Guid sessionId, CancellationToken ct)
    {
        var count = await service.RetryFailedAsync(sessionId, ct);
        return Ok(new ApiResponse<object>(true, new { requeued = count }));
    }
}
