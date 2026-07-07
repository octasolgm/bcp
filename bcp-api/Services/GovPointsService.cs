using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Reguliq.Api.Models;

namespace Reguliq.Api.Services;

public class GovPointsService(IWebHostEnvironment env, ILogger<GovPointsService> logger)
{
    private List<GovPoint>? _cache;
    private string _source = "seed";

    /// <summary>Where active points came from: seed | db-cache | extract-live</summary>
    public string Source => _source;

    public IReadOnlyList<GovPoint> GetAllPoints()
    {
        _cache ??= ExpandSubLeaves(LoadSeed());
        return _cache;
    }

    public void SetPoints(IReadOnlyList<GovPoint> points, string source)
    {
        _cache = ExpandSubLeaves(points.ToList());
        _source = source;
        logger.LogInformation("Gov points set from {Source} ({Count} raw)", source, points.Count);
    }

    public void ReloadFromSeed()
    {
        _cache = null;
        _source = "seed";
        _ = GetAllPoints();
    }

    public IReadOnlyList<GovPoint> FilterByGranularity(string granularity)
    {
        var all = GetAllPoints();
        return granularity == "section"
            ? FilterSectionPoints(all)
            : FilterLeafPoints(all);
    }

    public List<GovPoint> ResolveSelectedPoints(
        IEnumerable<string> pointIds,
        string granularity,
        IReadOnlyList<GovPoint>? clientPoints = null)
    {
        var idSet = pointIds
            .Select(id => id.Trim())
            .Where(id => id.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (idSet.Count == 0) return [];

        if (clientPoints is { Count: > 0 })
        {
            var fromClient = clientPoints
                .Where(p => idSet.Contains(p.PointId.Trim()))
                .ToList();
            if (fromClient.Count == idSet.Count) return fromClient;
        }

        var fromFiltered = FilterByGranularity(granularity)
            .Where(p => idSet.Contains(p.PointId.Trim()))
            .ToList();
        if (fromFiltered.Count > 0) return fromFiltered;

        return GetAllPoints()
            .Where(p => idSet.Contains(p.PointId.Trim()))
            .ToList();
    }

    private List<GovPoint> LoadSeed()
    {
        var path = Path.Combine(env.ContentRootPath, "SeedData", "gov-tfs-guidelines.extract.json");
        if (!File.Exists(path))
        {
            logger.LogWarning("Gov seed not found at {Path}", path);
            return [];
        }

        var json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);
        var points = new List<GovPoint>();
        if (doc.RootElement.TryGetProperty("points", out var arr))
        {
            foreach (var p in arr.EnumerateArray())
            {
                points.Add(new GovPoint(
                    p.GetProperty("point_id").GetString() ?? "",
                    p.TryGetProperty("title", out var t) ? t.GetString() : null,
                    p.GetProperty("text").GetString() ?? "",
                    p.TryGetProperty("section", out var s) ? s.GetString() : null));
            }
        }
        return points;
    }

    private static List<GovPoint> ExpandSubLeaves(IReadOnlyList<GovPoint> points)
    {
        var expanded = new List<GovPoint>();
        foreach (var point in points)
        {
            var norm = NormalizeNumericPointId(point.PointId);
            if (norm is null || norm.Split('.').Length != 2)
            {
                expanded.Add(point);
                continue;
            }

            var bullets = SplitObligationBullets(point.Text);
            if (bullets.Count < 2)
            {
                expanded.Add(point);
                continue;
            }

            var section = string.IsNullOrWhiteSpace(point.Section)
                ? (string.IsNullOrWhiteSpace(point.Title) ? $"{norm}." : $"{norm}. {point.Title}")
                : point.Section.Trim();

            for (var i = 0; i < bullets.Count; i++)
            {
                expanded.Add(new GovPoint(
                    $"{norm}.{i + 1}",
                    InferBulletTitle(bullets[i]) ?? point.Title,
                    bullets[i],
                    section));
            }
        }

        return expanded;
    }

    private static List<GovPoint> FilterLeafPoints(IReadOnlyList<GovPoint> points)
    {
        var allIds = points.Select(p => p.PointId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return points
            .Where(p => IsLeafComparable(p, allIds))
            .Where(p => !IsBareDefinitionLabel(p.PointId))
            .ToList();
    }

    private static List<GovPoint> FilterSectionPoints(IReadOnlyList<GovPoint> points)
    {
        var allIds = points.Select(p => p.PointId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return points
            .Where(p =>
            {
                var norm = NormalizeNumericPointId(p.PointId);
                if (norm is null) return p.PointId.StartsWith("Article", StringComparison.OrdinalIgnoreCase);
                var parts = norm.Split('.');
                return parts.Length == 2 && !HasNumericChildren(norm, allIds);
            })
            .Where(p => !IsBareDefinitionLabel(p.PointId))
            .ToList();
    }

    private static bool IsLeafComparable(GovPoint point, HashSet<string> allIds)
    {
        var id = point.PointId.Trim();
        if (IsBareDefinitionLabel(id)) return false;
        if (id.StartsWith("Article", StringComparison.OrdinalIgnoreCase)) return true;

        var norm = NormalizeNumericPointId(id);
        if (norm is null) return false;

        var parts = norm.Split('.');
        if (parts.Length >= 3) return true;
        if (parts.Length == 2) return !HasNumericChildren(norm, allIds);
        return false;
    }

    private static bool HasNumericChildren(string norm, HashSet<string> allIds) =>
        allIds.Any(other =>
            !string.Equals(other, norm, StringComparison.OrdinalIgnoreCase) &&
            other.StartsWith($"{norm}.", StringComparison.OrdinalIgnoreCase));

    private static bool IsBareDefinitionLabel(string pointId) =>
        Regex.IsMatch(pointId.Trim(), @"^\d+$");

    private static string? NormalizeNumericPointId(string pointId)
    {
        var id = pointId.Trim().TrimEnd('.');
        if (!Regex.IsMatch(id, @"^\d+(?:\.\d+)+$")) return null;
        return id;
    }

    private static string? InferBulletTitle(string bullet)
    {
        var head = bullet.Split(['.', ';'])[0]?.Trim();
        return head is { Length: > 0 and <= 72 } ? head : null;
    }

    private static List<string> SplitObligationBullets(string text)
    {
        var trimmed = text.Trim();
        if (string.IsNullOrEmpty(trimmed)) return [];

        if (Regex.IsMatch(trimmed, @"\s\*\s+") || Regex.IsMatch(trimmed, @"^\*\s+", RegexOptions.Multiline))
        {
            var parts = Regex.Split(trimmed, @"(?:^|\s)\*\s+")
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToList();
            if (parts.Count < 2) return [];
            var intro = parts[0];
            var bullets = parts.Skip(1).ToList();
            if (bullets.Count < 2) return [];
            if (intro.EndsWith(':'))
                return bullets;
            if (intro.Length > 40)
                return bullets.Select((b, i) => i == 0 ? $"{intro}: {b}" : b).ToList();
            return bullets;
        }

        var colonParts = Regex.Split(
            trimmed,
            @"(?=\b(?:Periodic|Ad hoc|Re-screening)\b[^:]*:)",
            RegexOptions.IgnoreCase);
        if (colonParts.Length >= 3)
        {
            var chunks = colonParts.Select(s => s.Trim()).Where(s => s.Length > 20).ToList();
            if (chunks.Count >= 2) return chunks;
        }

        var dashMatch = Regex.Match(trimmed, @":\s+(-\s+.+)$", RegexOptions.Singleline);
        if (dashMatch.Success)
        {
            var preamble = trimmed[..dashMatch.Index].Trim();
            var list = trimmed[(dashMatch.Index + 1)..].Trim();
            var items = Regex.Split(list, @"\s+-\s+")
                .Select(s => s.TrimStart('-').Trim())
                .Where(s => s.Length > 0)
                .ToList();
            if (items.Count >= 2)
                return items.Select(item => $"{preamble}: {item}").ToList();
        }

        return [];
    }
}

public static class DualVerifyPromptBuilder
{
    public static string Build(GovPoint point, string landingMessage, string? markdownSupplement = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("DUAL VERIFICATION PIPELINE — PASS 2 (INDEPENDENT)");
        sb.AppendLine("You are the second verifier. Landing AI (Pass 1) already analyzed this requirement.");
        sb.AppendLine("Re-read the attached internal PDF(s) and produce your own assessment.");
        sb.AppendLine();
        sb.AppendLine("LANDING AI PASS 1 (reference only):");
        sb.AppendLine("---");
        sb.AppendLine(landingMessage.Trim());
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("REQUIREMENT POINT TO CHECK:");
        sb.AppendLine(FormatPoint(point));
        if (!string.IsNullOrWhiteSpace(markdownSupplement))
        {
            sb.AppendLine();
            sb.AppendLine("ADDITIONAL INTERNAL DOCUMENT:");
            sb.AppendLine("---");
            sb.AppendLine(markdownSupplement.Trim());
            sb.AppendLine("---");
        }
        return sb.ToString();
    }

    private static string FormatPoint(GovPoint point)
    {
        var head = !string.IsNullOrWhiteSpace(point.Title)
            ? $"{point.PointId} {point.Title}"
            : point.PointId;
        return $"{head}\n{point.Text}".Trim();
    }
}
