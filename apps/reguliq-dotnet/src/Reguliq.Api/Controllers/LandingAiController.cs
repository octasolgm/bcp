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
            section = p.Section,
            point_type = "mandatory"
        });
        return Ok(new { success = true, cached = true, pointCount = points.Count(), points, docId = docId ?? "gov-tfs-guidelines" });
    }

    [HttpPost("seed/builtin")]
    public IActionResult SeedBuiltin() =>
        Ok(new { success = true, message = "Gov points loaded from embedded seed (NestJS seed for Supabase cache: start api on :4000)" });

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

        var filtered = sessions.Where(s => MatchesGranularity(s.SummaryJson, granularity));

        return Ok(new
        {
            success = true,
            sessions = filtered.Select(s => new
            {
                id = s.Id,
                label = $"{granularity} · {s.ComparedPoints} points · {s.UpdatedAt:yyyy-MM-dd HH:mm}",
                comparedPoints = s.ComparedPoints,
                granularity,
                source = "db",
                updatedAt = s.UpdatedAt.ToString("o")
            })
        });
    }

    [HttpGet("compliance-sessions/{id:guid}")]
    public async Task<IActionResult> GetComplianceSession(Guid id, [FromQuery] string? granularity, CancellationToken ct)
    {
        var session = await db.ComplianceSessions.FindAsync([id], ct);
        if (session == null) return NotFound(new { success = false, message = "Not found" });

        var results = JsonSerializer.Deserialize<object>(session.ResultsJson);
        return Ok(new
        {
            success = true,
            source = "db",
            id = session.Id,
            govFileName = session.GovFileName,
            internalFileName = session.InternalFileName,
            comparedPoints = session.ComparedPoints,
            totalGovPoints = session.TotalGovPoints,
            skippedPoints = session.SkippedPoints,
            results,
            summaryJson = string.IsNullOrWhiteSpace(session.SummaryJson)
                ? null
                : JsonSerializer.Deserialize<object>(session.SummaryJson)
        });
    }

    [HttpPost("compliance-sessions")]
    public async Task<IActionResult> SaveComplianceSession([FromBody] JsonElement body, CancellationToken ct)
    {
        var govHash = body.GetProperty("govFileHash").GetString() ?? "";
        var internalHash = body.GetProperty("internalFileHash").GetString() ?? "";
        var granularity = body.TryGetProperty("compareGranularity", out var g)
            ? g.GetString() ?? "dual-leaf"
            : "dual-leaf";
        var sessionKey = ComputeSessionKey(govHash, internalHash, granularity);

        var existing = await db.ComplianceSessions.FirstOrDefaultAsync(s => s.SessionKey == sessionKey, ct);
        var incomingResults = body.TryGetProperty("resultsJson", out var rj)
            ? rj.GetRawText()
            : "[]";

        var merged = false;
        if (existing == null)
        {
            existing = new Data.Entities.ComplianceSession
            {
                Id = Guid.NewGuid(),
                SessionKey = sessionKey,
                GovFileHash = govHash,
                InternalFileHash = internalHash,
                GovFileName = body.TryGetProperty("govFileName", out var gfn) ? gfn.GetString() : null,
                InternalFileName = body.TryGetProperty("internalFileName", out var ifn) ? ifn.GetString() : null,
                TotalGovPoints = body.TryGetProperty("totalGovPoints", out var tgp) ? tgp.GetInt32() : 0,
                ComparedPoints = body.TryGetProperty("comparedPoints", out var cp) ? cp.GetInt32() : 0,
                SkippedPoints = body.TryGetProperty("skippedPoints", out var sp) ? sp.GetInt32() : 0,
                ResultsJson = incomingResults,
                SummaryJson = body.TryGetProperty("summaryJson", out var sj) ? sj.GetRawText() : null,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.ComplianceSessions.Add(existing);
        }
        else
        {
            merged = true;
            var existingList = JsonSerializer.Deserialize<List<JsonElement>>(existing.ResultsJson) ?? [];
            var incomingList = JsonSerializer.Deserialize<List<JsonElement>>(incomingResults) ?? [];
            var map = new Dictionary<string, JsonElement>();
            foreach (var item in existingList)
            {
                if (item.TryGetProperty("point_id", out var pid))
                    map[pid.GetString() ?? ""] = item;
            }
            foreach (var item in incomingList)
            {
                if (item.TryGetProperty("point_id", out var pid))
                    map[pid.GetString() ?? ""] = item;
            }
            existing.ResultsJson = JsonSerializer.Serialize(map.Values);
            existing.ComparedPoints = map.Count;
            existing.UpdatedAt = DateTime.UtcNow;
            db.ComplianceSessions.Update(existing);
        }

        await db.SaveChangesAsync(ct);
        return Ok(new
        {
            success = true,
            source = "db",
            sessionKey,
            compareGranularity = granularity,
            comparedPoints = existing.ComparedPoints,
            merged,
            message = merged ? "Merged into existing compliance session" : "Saved new compliance session"
        });
    }

    private static bool MatchesGranularity(string? summaryJson, string granularity)
    {
        if (string.IsNullOrWhiteSpace(summaryJson)) return granularity.StartsWith("dual");
        try
        {
            using var doc = JsonDocument.Parse(summaryJson);
            if (doc.RootElement.TryGetProperty("granularity", out var g))
            {
                var val = g.GetString() ?? "";
                return val.Contains(granularity.Replace("dual-", ""));
            }
        }
        catch { /* include */ }
        return true;
    }

    private static string ComputeSessionKey(string govHash, string internalHash, string granularity)
    {
        var input = $"{govHash}:{internalHash}:{granularity}";
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
