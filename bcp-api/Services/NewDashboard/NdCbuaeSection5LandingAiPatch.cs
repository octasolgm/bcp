using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;

namespace Reguliq.Api.Services.NewDashboard;

/// <summary>
/// Restores CBUAE section 5 points from the original Landing AI extract when missing from the DB.
/// Not a full regulation seed — only the §5 gap (5, 5.1–5.4).
/// </summary>
public sealed class NdCbuaeSection5LandingAiPatch(IHostEnvironment env)
{
    public const string PatchFileName = "cbuae-section-5-patch.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static List<Section5PatchRow>? _cache;
    private static readonly object CacheLock = new();

    public sealed record Section5PatchRow(
        string PointNumber,
        string? PointTitle,
        string PointContent,
        string? PageReference,
        bool IsIntroductionPoint,
        bool IsAnnexPoint);

    public List<Section5PatchRow> LoadPatchRows()
    {
        lock (CacheLock)
        {
            if (_cache != null)
                return _cache;

            var path = Path.Combine(env.ContentRootPath, "SeedData", PatchFileName);
            if (!File.Exists(path))
                return _cache = [];

            var json = File.ReadAllText(path);
            _cache = JsonSerializer.Deserialize<List<Section5PatchRow>>(json, JsonOptions) ?? [];
            return _cache;
        }
    }

    public static bool IsCbuaeRegulationDocument(string? name) =>
        DemoAnalysisSeedService.IsCbuaeRegulationName(name);

    /// <returns>Number of section-5 rows inserted.</returns>
    public int ApplyMissing(
        Guid regulationDocumentId,
        ICollection<NdRegulationPoint> activePoints,
        AppDbContext db)
    {
        var patchRows = LoadPatchRows();
        if (patchRows.Count == 0)
            return 0;

        var existing = new HashSet<string>(
            activePoints.Select(p => GovPointExtractNormalizer.NormalizePointNumberKey(p.PointNumber)),
            StringComparer.OrdinalIgnoreCase);

        var added = 0;
        foreach (var row in patchRows)
        {
            var key = GovPointExtractNormalizer.NormalizePointNumberKey(row.PointNumber);
            if (string.IsNullOrWhiteSpace(key) || existing.Contains(key))
                continue;

            db.NdRegulationPoints.Add(new NdRegulationPoint
            {
                RegulationDocumentId = regulationDocumentId,
                PointNumber = row.PointNumber.Trim(),
                PointTitle = row.PointTitle,
                PointContent = row.PointContent.Trim(),
                PageReference = row.PageReference,
                IsIntroductionPoint = row.IsIntroductionPoint,
                IsAnnexPoint = row.IsAnnexPoint,
                Status = NdRegulationPointStatus.Active,
            });
            existing.Add(key);
            added++;
        }

        return added;
    }

    public static bool HasSection5Points(IEnumerable<NdRegulationPoint> activePoints) =>
        activePoints.Any(p =>
            GovPointExtractNormalizer.NormalizePointNumberKey(p.PointNumber) is var key
            && (key == "5" || key.StartsWith("5.", StringComparison.Ordinal)));
}
