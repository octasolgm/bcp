using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Models;

namespace Reguliq.Api.Services;

public class DashboardService(IServiceScopeFactory scopeFactory)
{
    public async Task<DashboardMetricsDto> GetMetricsAsync(CancellationToken ct = default)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<DualVerifyStoreService>();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var (compliant, partial, nonCompliant, lastDate) = await AggregateFromKafkaSessionsAsync(store, ct);
            if (compliant + partial + nonCompliant == 0)
            {
                var fromDb = await AggregateFromComplianceSessionsAsync(db, ct);
                compliant = fromDb.compliant;
                partial = fromDb.partial;
                nonCompliant = fromDb.nonCompliant;
                lastDate = fromDb.lastDate ?? lastDate;
            }

            var total = compliant + partial + nonCompliant;
            var breakdown = BuildBreakdown(compliant, partial, nonCompliant);
            var recent = await BuildRecentAnalysesAsync(store, db, ct);

            return new DashboardMetricsDto(
                compliant,
                partial,
                nonCompliant,
                total,
                lastDate ?? DateTime.UtcNow.ToString("yyyy-MM-dd"),
                breakdown,
                recent);
        }
        catch
        {
            return new DashboardMetricsDto(
                0, 0, 0, 0,
                DateTime.UtcNow.ToString("yyyy-MM-dd"),
                [],
                []);
        }
    }

    private static List<ComplianceBreakdownItem> BuildBreakdown(int compliant, int partial, int nonCompliant)
    {
        var items = new List<ComplianceBreakdownItem>();
        if (compliant > 0) items.Add(new("Compliant", compliant, "#22c55e"));
        if (partial > 0) items.Add(new("Partial", partial, "#eab308"));
        if (nonCompliant > 0) items.Add(new("Non-compliant", nonCompliant, "#ef4444"));
        return items;
    }

    private static async Task<(int compliant, int partial, int nonCompliant, string? lastDate)>
        AggregateFromKafkaSessionsAsync(DualVerifyStoreService store, CancellationToken ct)
    {
        var sessions = await store.ListRecentAsync(50, ct);
        var latest = sessions.FirstOrDefault(s => s.CompletedPoints > 0);
        if (latest == null) return (0, 0, 0, null);

        var full = await store.GetSessionAsync(latest.Id, ct);
        if (full?.PointJobs == null || full.PointJobs.Count == 0)
            return (0, 0, 0, latest.UpdatedAt.ToString("yyyy-MM-dd"));

        var counts = CountComplianceBuckets(full.PointJobs
            .Where(p => p.Status == "completed")
            .Select(p => p.AgreementJson));

        return (counts.compliant, counts.partial, counts.nonCompliant, latest.UpdatedAt.ToString("yyyy-MM-dd"));
    }

    private static async Task<(int compliant, int partial, int nonCompliant, string? lastDate)>
        AggregateFromComplianceSessionsAsync(AppDbContext db, CancellationToken ct)
    {
        try
        {
            var session = await db.ComplianceSessions
                .OrderByDescending(s => s.UpdatedAt)
                .FirstOrDefaultAsync(ct);
            if (session == null || string.IsNullOrWhiteSpace(session.ResultsJson))
                return (0, 0, 0, null);

            var counts = CountComplianceBucketsFromResults(session.ResultsJson);
            return (counts.compliant, counts.partial, counts.nonCompliant, session.UpdatedAt.ToString("yyyy-MM-dd"));
        }
        catch
        {
            return (0, 0, 0, null);
        }
    }

    private static async Task<List<RecentAnalysisDto>> BuildRecentAnalysesAsync(
        DualVerifyStoreService store,
        AppDbContext db,
        CancellationToken ct)
    {
        var rows = new List<RecentAnalysisDto>();

        foreach (var session in (await store.ListRecentAsync(8, ct)).Where(s => s.CompletedPoints > 0))
        {
            var full = await store.GetSessionAsync(session.Id, ct);
            if (full?.PointJobs == null) continue;
            var counts = CountComplianceBuckets(full.PointJobs
                .Where(p => p.Status == "completed")
                .Select(p => p.AgreementJson));
            var findings = counts.compliant + counts.partial + counts.nonCompliant;
            if (findings == 0) continue;
            rows.Add(new RecentAnalysisDto(
                session.Id.ToString(),
                $"Dual verify · {session.Granularity}",
                session.UpdatedAt.ToString("yyyy-MM-dd"),
                findings,
                counts.compliant,
                counts.partial,
                counts.nonCompliant));
        }

        if (rows.Count > 0) return rows;

        try
        {
            var saved = await db.ComplianceSessions
                .OrderByDescending(s => s.UpdatedAt)
                .Take(5)
                .ToListAsync(ct);
            foreach (var s in saved)
            {
                if (string.IsNullOrWhiteSpace(s.ResultsJson)) continue;
                var counts = CountComplianceBucketsFromResults(s.ResultsJson);
                var findings = counts.compliant + counts.partial + counts.nonCompliant;
                if (findings == 0) continue;
                rows.Add(new RecentAnalysisDto(
                    s.Id.ToString(),
                    $"{s.GovFileName ?? "Compliance"} · saved",
                    s.UpdatedAt.ToString("yyyy-MM-dd"),
                    findings,
                    counts.compliant,
                    counts.partial,
                    counts.nonCompliant));
            }
        }
        catch { /* sqlite optional */ }

        return rows;
    }

    private static (int compliant, int partial, int nonCompliant) CountComplianceBuckets(
        IEnumerable<string?> agreementJsonList)
    {
        var compliant = 0;
        var partial = 0;
        var nonCompliant = 0;
        foreach (var json in agreementJsonList)
        {
            switch (BucketFromAgreementJson(json))
            {
                case "compliant": compliant++; break;
                case "partial": partial++; break;
                case "non-compliant": nonCompliant++; break;
            }
        }
        return (compliant, partial, nonCompliant);
    }

    private static (int compliant, int partial, int nonCompliant) CountComplianceBucketsFromResults(string resultsJson)
    {
        var compliant = 0;
        var partial = 0;
        var nonCompliant = 0;
        try
        {
            using var doc = JsonDocument.Parse(resultsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return (0, 0, 0);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var status = ExtractPass2Status(item);
                switch (NormalizeStatus(status))
                {
                    case "compliant": compliant++; break;
                    case "partial": partial++; break;
                    case "non-compliant": nonCompliant++; break;
                }
            }
        }
        catch { /* ignore malformed */ }
        return (compliant, partial, nonCompliant);
    }

    private static string? ExtractPass2Status(JsonElement item)
    {
        if (item.TryGetProperty("agreementJson", out var ag) && ag.ValueKind == JsonValueKind.Object)
        {
            if (ag.TryGetProperty("llmStatus", out var llm)) return llm.GetString();
            if (ag.TryGetProperty("LlmStatus", out var llmPascal)) return llmPascal.GetString();
        }
        if (item.TryGetProperty("llmMessage", out var msg))
            return ExtractStatusFromMessage(msg.GetString());
        if (item.TryGetProperty("message", out var legacy))
            return ExtractStatusFromMessage(legacy.GetString());
        return null;
    }

    private static string BucketFromAgreementJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var status = root.TryGetProperty("llmStatus", out var llm)
                ? llm.GetString()
                : root.TryGetProperty("LlmStatus", out var llmPascal)
                    ? llmPascal.GetString()
                    : null;
            return NormalizeStatus(status);
        }
        catch
        {
            return "";
        }
    }

    private static string? ExtractStatusFromMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        var match = System.Text.RegularExpressions.Regex.Match(
            message,
            @"Comply Yes/No \(Status\)\s*:\s*(.+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static string NormalizeStatus(string? status)
    {
        var s = (status ?? "").Trim();
        if (string.IsNullOrEmpty(s)) return "";
        if (s.Equals("Compliant", StringComparison.OrdinalIgnoreCase) &&
            !s.Contains("partial", StringComparison.OrdinalIgnoreCase))
            return "compliant";
        if (s.Contains("partial", StringComparison.OrdinalIgnoreCase)) return "partial";
        if (System.Text.RegularExpressions.Regex.IsMatch(s, @"non[- ]?compliant", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return "non-compliant";
        return "";
    }
}
