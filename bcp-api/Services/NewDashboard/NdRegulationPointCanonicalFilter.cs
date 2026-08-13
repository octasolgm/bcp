using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;

namespace Reguliq.Api.Services.NewDashboard;

/// <summary>
/// Derive display/gap-analysis point counts without duplicate glossary rows or junk extract ids.
/// Uses the same rules as <see cref="NdRegulationPointRepairService"/> repair planning.
/// </summary>
public static class NdRegulationPointCanonicalFilter
{
    public static HashSet<Guid> SelectKeepIds(IReadOnlyList<NdRegulationPoint> points, bool isManual = false)
    {
        if (points.Count == 0)
            return [];
        if (isManual)
            return points.Select(p => p.Id).ToHashSet();

        var plan = GovPointExtractNormalizer.PlanRepair(
            points,
            p => p.Id,
            p => p.PointNumber,
            p => p.PointTitle,
            p => p.PointContent ?? "",
            p => ParsePageFromReference(p.PageReference));
        return plan.KeepIds.ToHashSet();
    }

    public static int CountCanonical(IReadOnlyList<NdRegulationPoint> points, bool isManual = false) =>
        SelectKeepIds(points, isManual).Count;

    public static List<NdRegulationPoint> FilterCanonical(
        IEnumerable<NdRegulationPoint> points,
        bool isManual = false)
    {
        var list = points as IReadOnlyList<NdRegulationPoint> ?? points.ToList();
        if (list.Count == 0)
            return [];
        var keep = SelectKeepIds(list, isManual);
        return list.Where(p => keep.Contains(p.Id)).ToList();
    }

    public static async Task<Dictionary<Guid, int>> BuildCanonicalCountMapAsync(
        AppDbContext db,
        IReadOnlyCollection<Guid> regulationDocumentIds,
        IReadOnlyDictionary<Guid, bool> manualByDocId,
        CancellationToken ct)
    {
        if (regulationDocumentIds.Count == 0)
            return new Dictionary<Guid, int>();

        var rows = await db.NdRegulationPoints.AsNoTracking()
            .Where(p =>
                regulationDocumentIds.Contains(p.RegulationDocumentId)
                && p.Status == NdRegulationPointStatus.Active)
            .ToListAsync(ct);

        return rows
            .GroupBy(p => p.RegulationDocumentId)
            .ToDictionary(
                g => g.Key,
                g => CountCanonical(
                    g.ToList(),
                    manualByDocId.GetValueOrDefault(g.Key)));
    }

    public static async Task<int> CountCanonicalForDocumentAsync(
        AppDbContext db,
        Guid regulationDocumentId,
        bool isManual,
        CancellationToken ct)
    {
        var points = await db.NdRegulationPoints.AsNoTracking()
            .Where(p => p.RegulationDocumentId == regulationDocumentId
                && p.Status == NdRegulationPointStatus.Active)
            .ToListAsync(ct);
        return CountCanonical(points, isManual);
    }

    private static int? ParsePageFromReference(string? pageReference)
    {
        if (string.IsNullOrWhiteSpace(pageReference)) return null;
        var m = Regex.Match(pageReference, @"p\.\s*(\d+)", RegexOptions.IgnoreCase);
        return m.Success && int.TryParse(m.Groups[1].Value, out var page) ? page : null;
    }
}
