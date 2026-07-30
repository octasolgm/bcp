using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Models;

namespace Reguliq.Api.Services.NewDashboard;

/// <summary>
/// Build the gov point sent to Phase 1/2 — prefer full regulation_points row over stale/short snapshots.
/// </summary>
public static class NdAnalysisGovPointResolver
{
    public static async Task<GovPoint> ResolveAsync(
        AppDbContext db,
        Guid? regulationPointId,
        PointSnapshotDto snapshot,
        CancellationToken ct = default)
    {
        var number = FirstNonEmpty(snapshot.PointNumber, snapshot.PointId) ?? "";
        var title = snapshot.PointTitle;
        var content = snapshot.PointContent ?? "";
        var section = snapshot.PageReference;

        if (regulationPointId is Guid regId)
        {
            var reg = await db.NdRegulationPoints.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == regId, ct);
            if (reg != null)
            {
                var regContent = (reg.PointContent ?? "").Trim();
                var snapNum = (snapshot.PointNumber ?? "").Trim();
                var regNum = (reg.PointNumber ?? "").Trim();
                var snapIsDistinctLeaf = GovPointExtractNormalizer.IsValidExtractPointId(snapNum)
                    && !string.Equals(snapNum, regNum, StringComparison.OrdinalIgnoreCase);

                if (!snapIsDistinctLeaf && regContent.Length > content.Trim().Length)
                    content = regContent;

                if (!snapIsDistinctLeaf && !string.IsNullOrWhiteSpace(reg.PointTitle))
                {
                    title = string.IsNullOrWhiteSpace(title) || title.Trim().Length < reg.PointTitle.Trim().Length
                        ? reg.PointTitle
                        : title;
                }

                if (!GovPointExtractNormalizer.IsJunkExtractPointId(reg.PointNumber)
                    && GovPointExtractNormalizer.IsValidExtractPointId(reg.PointNumber)
                    && (string.IsNullOrWhiteSpace(snapNum)
                        || string.Equals(snapNum, reg.PointNumber.Trim(), StringComparison.OrdinalIgnoreCase)))
                {
                    number = reg.PointNumber.Trim();
                }
                else if (GovPointExtractNormalizer.IsValidExtractPointId(snapNum))
                {
                    number = snapNum;
                }

                section ??= reg.PageReference;
            }
        }

        if (GovPointExtractNormalizer.IsJunkExtractPointId(number))
        {
            var fromSection = ExtractLeadingClause(section);
            if (!string.IsNullOrWhiteSpace(fromSection))
                number = fromSection;
        }

        return new GovPoint(number, title, content, section);
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        }

        return null;
    }

    private static string? ExtractLeadingClause(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim();
        var m = System.Text.RegularExpressions.Regex.Match(
            s,
            @"^(?:§\s*)?(\d+\.\d+(?:\.\d+)*)\.?(?:\s|[.—–\-:)]|$)");
        return m.Success ? m.Groups[1].Value : null;
    }
}
