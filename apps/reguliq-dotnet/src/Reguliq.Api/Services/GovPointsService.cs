using System.Text;
using System.Text.Json;
using Reguliq.Api.Models;

namespace Reguliq.Api.Services;

public class GovPointsService(IWebHostEnvironment env, ILogger<GovPointsService> logger)
{
    private List<GovPoint>? _cache;

    public IReadOnlyList<GovPoint> GetAllPoints()
    {
        _cache ??= LoadSeed();
        return _cache;
    }

    public IReadOnlyList<GovPoint> FilterByGranularity(string granularity)
    {
        var all = GetAllPoints();
        return granularity == "section"
            ? FilterSectionPoints(all)
            : FilterLeafPoints(all);
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

    private static List<GovPoint> FilterLeafPoints(IReadOnlyList<GovPoint> points) =>
        points.Where(p => p.PointId.Split('.').Length >= 3).ToList();

    private static List<GovPoint> FilterSectionPoints(IReadOnlyList<GovPoint> points) =>
        points.Where(p => p.PointId.Split('.').Length == 2).ToList();
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
