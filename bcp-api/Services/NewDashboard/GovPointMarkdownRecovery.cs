using Reguliq.Api.Models;
using Reguliq.Api.Services.LandingAi;

namespace Reguliq.Api.Services.NewDashboard;

/// <summary>Backfill regulation points Landing extract missed by scanning parse markdown headings.</summary>
public static class GovPointMarkdownRecovery
{
    public static List<GovPoint> MergeMissing(IReadOnlyList<GovPoint> extracted, string? markdown)
    {
        if (extracted.Count == 0 && string.IsNullOrWhiteSpace(markdown))
            return [];

        var result = extracted.ToList();
        var existing = new HashSet<string>(
            result.Select(p => GovPointExtractNormalizer.NormalizePointNumberKey(p.PointId)),
            StringComparer.OrdinalIgnoreCase);

        foreach (var scanned in MarkdownSectionScanner.Scan(markdown))
        {
            var pointId = MarkdownSectionScanner.NormalizeRef(scanned.SectionRef);
            if (!GovPointExtractNormalizer.IsValidExtractPointId(pointId)) continue;
            if (existing.Contains(GovPointExtractNormalizer.NormalizePointNumberKey(pointId))) continue;

            var title = ExtractTitle(scanned.SectionText, pointId);
            result.Add(new GovPoint(
                pointId,
                title,
                scanned.SectionText,
                $"{pointId}. {title}",
                null,
                "mandatory"));
            existing.Add(GovPointExtractNormalizer.NormalizePointNumberKey(pointId));
        }

        return GovPointExtractNormalizer.DedupeAndFilter(result);
    }

    private static string? ExtractTitle(string sectionText, string pointId)
    {
        var firstLine = sectionText.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? "";
        var prefix = pointId + ".";
        if (firstLine.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return firstLine[prefix.Length..].Trim();
        if (firstLine.StartsWith(pointId, StringComparison.OrdinalIgnoreCase))
        {
            var rest = firstLine[pointId.Length..].TrimStart('.', ':', ' ', '-', '–');
            return rest.Length > 0 ? rest : null;
        }

        return null;
    }
}
