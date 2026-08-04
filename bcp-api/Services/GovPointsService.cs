using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Reguliq.Api.Models;
using Reguliq.Api.Services.LandingAi;

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
    public static string Build(
        GovPoint point,
        string landingMessage,
        string? markdownSupplement = null,
        IReadOnlyList<string>? attachedFileNames = null,
        ComparePromptVersion version = ComparePromptVersion.V1)
    {
        var sb = new StringBuilder();
        sb.AppendLine("DUAL VERIFICATION PIPELINE — PASS 2 (INDEPENDENT)");
        sb.AppendLine("You are the second verifier. Landing AI (Pass 1) already analyzed this requirement.");
        sb.AppendLine("Re-read the attached internal PDF(s) and produce your own assessment.");
        if (version == ComparePromptVersion.V2)
            AppendPass2RulesV2(sb);
        else if (version == ComparePromptVersion.V3)
            AppendPass2RulesV3(sb);
        if (attachedFileNames is { Count: > 0 })
        {
            sb.AppendLine(attachedFileNames.Count == 1
                ? $"Attached PDF: {attachedFileNames[0]}"
                : $"Attached PDFs ({attachedFileNames.Count}): {string.Join(", ", attachedFileNames)}");
        }
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
            sb.AppendLine("INTERNAL DOCUMENT MARKDOWN (parsed text — use with attached PDF(s) for accuracy):");
            sb.AppendLine("---");
            sb.AppendLine(markdownSupplement.Trim());
            sb.AppendLine("---");
        }
        if (version is ComparePromptVersion.V2 or ComparePromptVersion.V3)
            AppendPass2OutputFormatV2(sb);

        return sb.ToString();
    }

    private static void AppendPass2RulesV2(StringBuilder sb)
    {
        sb.AppendLine();
        sb.AppendLine("Pass 2 rules (V2):");
        sb.AppendLine("- Independently search ALL attached internal PDF(s) and markdown for evidence on EVERY sub-obligation.");
        sb.AppendLine("- Use the same semantic standards as Pass 1 — confirm or correct, not stricter keyword matching.");
        sb.AppendLine("- Search every attached document before concluding Non-Compliant. Compliant if any document satisfies all sub-obligations.");
        sb.AppendLine("- If Pass 1 evidence is accurate and complete, align with the same status and similar confidence.");
        sb.AppendLine("- Cite each source with: [Document Name], Section [X], Page [N]: \"verbatim quote\". One line per document/page when multiple sources apply.");
    }

    /// <summary>Read-only Pass 2 V3 rules text for admin prompt review.</summary>
    public static string GetPass2RulesV3Text()
    {
        var sb = new StringBuilder();
        AppendPass2RulesV3(sb);
        return sb.ToString().TrimEnd();
    }

    /// <summary>Regul.ai judgment rules for Pass 2 (analyse-v9 / V3).</summary>
    private static void AppendPass2RulesV3(StringBuilder sb)
    {
        sb.AppendLine();
        sb.AppendLine("Pass 2 rules (V3 — Regul.ai judgment):");
        sb.AppendLine("- Independently re-judge the clause against ALL attached internal PDF(s) and markdown. Confirm or correct Pass 1; do not copy it blindly.");
        sb.AppendLine("- Document-perspective: the bank IMPLEMENTS the regulator's requirements — it is not expected to restate regulator-only content (other entity types, legal disclaimers, supervisor instructions). Omitting that content is NEVER a gap — mark Compliant with a note.");
        sb.AppendLine("- Vendor/list-provider due diligence means verifying the vendor-supplied list's accuracy/completeness against required source lists — NOT general procurement/vendor onboarding. Do not invent a vendor-vetting requirement the clause never asked for.");
        sb.AppendLine("- Element-level checking: for multi-element clauses, assess each element separately (covered / not covered with evidence). Compliant only if every element is covered; Partial if some; Non-Compliant if none.");
        sb.AppendLine("- Quotes must be VERBATIM from the internal text. Prefer lower confidence / Partial or Non-Compliant when evidence is weak rather than assuming coverage.");
        sb.AppendLine("- Gap text is MANDATORY when Partial or Non-Compliant: state exactly what is missing and which document it was / was not found in.");
        sb.AppendLine("- Cite each source with: [Document Name], Section [X], Page [N]: \"verbatim quote\". One line per document/page when multiple sources apply.");
    }

    private static void AppendPass2OutputFormatV2(StringBuilder sb)
    {
        sb.AppendLine();
        sb.AppendLine("Your response MUST use exactly this block format (field labels matter for automated comparison):");
        sb.AppendLine();
        sb.AppendLine("[point_id and title]");
        sb.AppendLine("[full requirement text]");
        sb.AppendLine();
        sb.AppendLine("Reference PDF :");
        sb.AppendLine("[document file name(s) with evidence — comma-separate when multiple]");
        sb.AppendLine();
        sb.AppendLine("Output/Response :");
        sb.AppendLine("[Document Name], Section [X], Page [N]: \"verbatim quote\"");
        sb.AppendLine("(one line per source when multiple documents/pages apply; or exactly: No corresponding procedure found.)");
        sb.AppendLine();
        sb.AppendLine("Fulfilled clauses :");
        sb.AppendLine("• [sub-obligation] — [Document Name], Section [X], Page [N]: \"quote\"");
        sb.AppendLine("(use None only if nothing is covered)");
        sb.AppendLine();
        sb.AppendLine("Comply Yes/No (Status) : Compliant | Partial Compliant | Non-Compliant");
        sb.AppendLine("Compliance Confidence % : [0-100]%");
        sb.AppendLine("Corrective Action Plan :");
        sb.AppendLine("Gap(s): ... OR empty / N/A if Compliant");
        sb.AppendLine("Responsibility :");
        sb.AppendLine("[role] OR empty / N/A if Compliant");
    }

    private static string FormatPoint(GovPoint point)
    {
        var head = !string.IsNullOrWhiteSpace(point.Title)
            ? $"{point.PointId} {point.Title}"
            : point.PointId;
        return $"{head}\n{point.Text}".Trim();
    }
}
