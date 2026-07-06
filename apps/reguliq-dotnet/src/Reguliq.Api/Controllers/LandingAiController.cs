using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Services;
using System.Text.Json;

namespace Reguliq.Api.Controllers;

[ApiController]
[Route("landing-ai")]
public class LandingAiController(GovPointsService govPoints, AppDbContext db) : ControllerBase
{
    [HttpGet("stored-points")]
    public IActionResult GetStoredPoints([FromQuery] string? docId)
    {
        var points = govPoints.GetAllPoints().Select(p => new
        {
            point_id = p.PointId,
            title = p.Title,
            text = p.Text,
            section = p.Section
        });
        return Ok(new { success = true, points });
    }

    [HttpPost("seed/builtin")]
    public IActionResult SeedBuiltin() =>
        Ok(new { success = true, message = "Gov points loaded from embedded seed" });

    [HttpGet("compliance-sessions")]
    public async Task<IActionResult> ListComplianceSessions(
        [FromQuery] int limit = 30,
        [FromQuery] string granularity = "dual-leaf",
        CancellationToken ct = default)
    {
        var sessions = await db.ComplianceSessions
            .OrderByDescending(s => s.UpdatedAt)
            .Take(limit)
            .ToListAsync(ct);

        var filtered = sessions.Where(s =>
        {
            if (string.IsNullOrWhiteSpace(s.SummaryJson)) return granularity.StartsWith("dual");
            try
            {
                using var doc = JsonDocument.Parse(s.SummaryJson);
                if (doc.RootElement.TryGetProperty("granularity", out var g))
                    return g.GetString()?.Contains(granularity.Replace("dual-", "")) == true;
            }
            catch { /* include */ }
            return true;
        });

        return Ok(new
        {
            sessions = filtered.Select(s => new
            {
                id = s.Id,
                label = $"{granularity} · {s.ComparedPoints} points · {s.UpdatedAt:yyyy-MM-dd}",
                comparedPoints = s.ComparedPoints,
                source = "db"
            })
        });
    }

    [HttpGet("compliance-sessions/{id:guid}")]
    public async Task<IActionResult> GetComplianceSession(Guid id, [FromQuery] string granularity, CancellationToken ct)
    {
        var session = await db.ComplianceSessions.FindAsync([id], ct);
        if (session == null) return NotFound(new { message = "Not found" });
        var results = JsonSerializer.Deserialize<object>(session.ResultsJson);
        return Ok(new { results, comparedPoints = session.ComparedPoints });
    }

    [HttpPost("compliance-sessions")]
    public async Task<IActionResult> SaveComplianceSession([FromBody] JsonElement body, CancellationToken ct)
    {
        // Accept frontend save payload — merge handled client-side; store as new row if needed
        return Ok(new { success = true, comparedPoints = body.TryGetProperty("comparedPoints", out var cp) ? cp.GetInt32() : 0 });
    }
}
