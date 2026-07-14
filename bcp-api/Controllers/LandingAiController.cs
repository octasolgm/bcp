using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Models;
using Reguliq.Api.Services;
using Reguliq.Api.Services.LandingAi;
using System.Text.Json;

namespace Reguliq.Api.Controllers;

[ApiController]
[Route("landing-ai")]
public class LandingAiController(
    GovPointsService govPoints,
    LandingAiGovExtractService govExtract,
    AppDbContext db,
    LandingAiCompareService landingAi) : ControllerBase
{
    [HttpGet("status")]
    public IActionResult GetStatus() =>
        Ok(new
        {
            configured = landingAi.IsConfigured,
            transport = "bcp-api",
            message = landingAi.IsConfigured
                ? "Landing AI Phase 1 ready (standalone bcp-api)"
                : "Set LandingAi:ApiKey in appsettings.Development.json",
        });

    [HttpGet("stored-parse")]
    public async Task<IActionResult> GetStoredParse([FromQuery] string fileHash, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(fileHash))
            return BadRequest(new { success = false, message = "fileHash is required" });

        var markdown = await landingAi.GetStoredParseAsync(fileHash.Trim(), ct);
        if (string.IsNullOrWhiteSpace(markdown))
            return NotFound(new { success = false, message = "No cached parse markdown for this fileHash" });

        return Ok(new { success = true, markdown, fileHash });
    }

    [HttpPost("compare-point")]
    [RequestSizeLimit(50_000_000)]
    public async Task<IActionResult> ComparePoint(CancellationToken ct)
    {
        try
        {
            var form = await Request.ReadFormAsync(ct);
            var pointJson = form["point"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(pointJson))
                return BadRequest(new { success = false, message = "point JSON is required" });

            using var pointDoc = JsonDocument.Parse(pointJson);
            var root = pointDoc.RootElement;
            var point = new GovPoint(
                root.GetProperty("point_id").GetString() ?? "",
                root.TryGetProperty("title", out var t) ? t.GetString() : null,
                root.GetProperty("text").GetString() ?? "",
                root.TryGetProperty("section", out var s) ? s.GetString() : null);

            var internalFileHash = form["internalFileHash"].FirstOrDefault() ?? "";
            var internalFileName = form["internalFileName"].FirstOrDefault() ?? "internal-policy.pdf";
            var forceCompare = string.Equals(form["forceCompare"].FirstOrDefault(), "true", StringComparison.OrdinalIgnoreCase);

            byte[]? pdf = null;
            var file = form.Files.GetFile("internalFile") ?? form.Files.FirstOrDefault();
            if (file != null)
            {
                using var ms = new MemoryStream();
                await file.CopyToAsync(ms, ct);
                pdf = ms.ToArray();
            }

            var message = await landingAi.ComparePointAsync(
                point, internalFileHash, internalFileName, pdf, forceCompare, ct);

            return Ok(new { success = true, cached = false, message });
        }
        catch (Exception ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

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
        return Ok(new
        {
            success = true,
            cached = true,
            pointCount = points.Count(),
            points,
            docId = docId ?? "gov-tfs-guidelines",
            source = govPoints.Source,
        });
    }

    [HttpPost("gov-points/load-from-db")]
    public async Task<IActionResult> LoadGovFromDb(
        [FromQuery] string docId = LandingAiGovExtractService.BuiltinGovDocId,
        CancellationToken ct = default)
    {
        try
        {
            var result = await govExtract.LoadFromDatabaseOrSeedAsync(docId, ct);
            var points = govPoints.GetAllPoints().Select(p => new
            {
                point_id = p.PointId,
                title = p.Title,
                text = p.Text,
                section = p.Section,
                point_type = "mandatory",
            });
            return Ok(new
            {
                success = result.Success,
                source = result.Source,
                pointCount = result.PointCount,
                activeSource = result.ActiveSource,
                points,
                docId,
                message = result.Source == "db-cache"
                    ? $"Loaded {result.PointCount} gov points from Supabase extract cache."
                    : $"No DB extract cache — loaded {result.PointCount} points from embedded seed.",
            });
        }
        catch (Exception ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    [HttpPost("extract-gov-points")]
    [RequestSizeLimit(50_000_000)]
    public async Task<IActionResult> ExtractGovPoints(
        [FromQuery] string? markdown,
        CancellationToken ct = default)
    {
        try
        {
            byte[]? pdf = null;
            var fileName = "gov-document.pdf";
            if (Request.HasFormContentType)
            {
                var form = await Request.ReadFormAsync(ct);
                var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
                if (file != null)
                {
                    fileName = file.FileName;
                    using var ms = new MemoryStream();
                    await file.CopyToAsync(ms, ct);
                    pdf = ms.ToArray();
                }
            }

            var result = await govExtract.ExtractFromUploadAsync(pdf, fileName, markdown, ct);
            return Ok(new
            {
                success = result.Success,
                cached = result.Cached,
                fileName = result.FileName,
                fileHash = result.FileHash,
                schemaKey = result.SchemaKey,
                pointCount = result.PointCount,
                points = result.Points,
                creditUsage = result.CreditUsage,
                source = result.Source,
            });
        }
        catch (Exception ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    [HttpPost("seed/builtin")]
    public IActionResult SeedBuiltin()
    {
        govPoints.ReloadFromSeed();
        return Ok(new
        {
            success = true,
            message = "Gov points loaded from embedded seed.",
            pointCount = govPoints.GetAllPoints().Count,
            source = govPoints.Source,
        });
    }

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

    [HttpDelete("compliance-sessions/{id:guid}")]
    public async Task<IActionResult> DeleteComplianceSession(Guid id, CancellationToken ct)
    {
        var session = await db.ComplianceSessions.FindAsync([id], ct);
        if (session == null)
            return NotFound(new { success = false, message = "Not found" });

        var analysisRuns = await db.DocumentAnalysisRuns
            .Where(r => r.ComplianceSessionId == id)
            .ToListAsync(ct);
        if (analysisRuns.Count > 0)
            db.DocumentAnalysisRuns.RemoveRange(analysisRuns);

        db.ComplianceSessions.Remove(session);
        await db.SaveChangesAsync(ct);
        return Ok(new { success = true, deleted = true, id });
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
            id = existing.Id,
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
