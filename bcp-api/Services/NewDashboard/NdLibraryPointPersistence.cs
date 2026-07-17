using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;

namespace Reguliq.Api.Services.NewDashboard;

public sealed record LibraryPointSyncInput(
    Guid RegulationPointId,
    Guid RegulationDocumentId,
    int DisplayOrder,
    object? PointSnapshot);

public sealed record PreparedLibraryPoint(
    Guid RegulationPointId,
    Guid RegulationDocumentId,
    int DisplayOrder,
    string? PointSnapshotJson);

public static class NdLibraryPointPersistence
{
    public static async Task<List<PreparedLibraryPoint>> PrepareAsync(
        AppDbContext db,
        Guid userId,
        IEnumerable<LibraryPointSyncInput> points,
        CancellationToken ct)
    {
        var prepared = new List<PreparedLibraryPoint>();
        var docIdCache = new Dictionary<Guid, Guid>();
        var assignedInBatch = new HashSet<Guid>();

        foreach (var point in points)
        {
            var regDocId = await ResolveRegulationDocumentIdCachedAsync(
                db, point.RegulationDocumentId, userId, docIdCache, ct);
            var resolvedPointId = await EnsureRegulationPointAsync(
                db, regDocId, point, assignedInBatch, ct);
            assignedInBatch.Add(resolvedPointId);
            prepared.Add(new PreparedLibraryPoint(
                resolvedPointId,
                regDocId,
                point.DisplayOrder,
                SerializeSnapshot(point.PointSnapshot)));
        }

        return prepared;
    }

    private static async Task<Guid> ResolveRegulationDocumentIdCachedAsync(
        AppDbContext db,
        Guid documentOrStoredId,
        Guid userId,
        Dictionary<Guid, Guid> cache,
        CancellationToken ct)
    {
        if (cache.TryGetValue(documentOrStoredId, out var cached))
            return cached;

        var ndDoc = await db.NdRegulationDocuments.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == documentOrStoredId, ct);
        if (ndDoc != null && !IsDepartmentOnlyOverlay(ndDoc))
        {
            cache[documentOrStoredId] = ndDoc.Id;
            return ndDoc.Id;
        }

        ndDoc = await db.NdRegulationDocuments.AsNoTracking()
            .FirstOrDefaultAsync(d => d.StoredDocumentId == documentOrStoredId, ct);
        if (ndDoc != null)
        {
            cache[documentOrStoredId] = ndDoc.Id;
            if (documentOrStoredId != ndDoc.Id)
                cache[ndDoc.Id] = ndDoc.Id;
            return ndDoc.Id;
        }

        var stored = await db.StoredDocuments.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == documentOrStoredId && d.DocKind == "regulation", ct);
        if (stored == null)
            throw new InvalidOperationException($"Regulation document {documentOrStoredId} was not found.");

        var overlay = new NdRegulationDocument
        {
            StoredDocumentId = stored.Id,
            Name = stored.Title,
            FilePath = stored.StoragePath ?? "",
            ExtractionStatus = (stored.PointCount ?? 0) > 0 ? "completed" : "pending",
            CreatedBy = userId,
        };
        db.NdRegulationDocuments.Add(overlay);
        cache[documentOrStoredId] = overlay.Id;
        cache[stored.Id] = overlay.Id;
        return overlay.Id;
    }

    private static async Task<Guid> EnsureRegulationPointAsync(
        AppDbContext db,
        Guid regulationDocumentId,
        LibraryPointSyncInput point,
        HashSet<Guid> assignedInBatch,
        CancellationToken ct)
    {
        ParseSnapshot(point.PointSnapshot, out var pointNumber, out var title, out var content, out var section, out var isIntro, out var isAnnex);
        if (string.IsNullOrWhiteSpace(pointNumber) && string.IsNullOrWhiteSpace(content))
            throw new InvalidOperationException($"Regulation point {point.RegulationPointId} is missing snapshot data.");

        var pointId = point.RegulationPointId;

        var contentMatch = await FindContentMatchAsync(
            db, regulationDocumentId, pointNumber, title, content, ct);
        if (contentMatch != null)
        {
            pointId = contentMatch.Value;
        }
        else
        {
            if (assignedInBatch.Contains(pointId))
                pointId = Guid.NewGuid();

            if (db.NdRegulationPoints.Local.Any(p => p.Id == pointId))
                pointId = Guid.NewGuid();

            var existingById = await db.NdRegulationPoints.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == pointId, ct);
            if (existingById != null)
            {
                if (PointContentMatches(existingById, pointNumber, title, content, regulationDocumentId))
                {
                    pointId = existingById.Id;
                }
                else
                {
                    pointId = Guid.NewGuid();
                }
            }

            if (db.NdRegulationPoints.Local.Any(p => p.Id == pointId))
                pointId = Guid.NewGuid();
        }

        await UpsertRegulationPointAsync(
            db,
            pointId,
            regulationDocumentId,
            pointNumber,
            title,
            content,
            section,
            isIntro,
            isAnnex,
            ct);

        return pointId;
    }

    private static async Task UpsertRegulationPointAsync(
        AppDbContext db,
        Guid pointId,
        Guid regulationDocumentId,
        string pointNumber,
        string? title,
        string content,
        string? section,
        bool isIntroductionPoint,
        bool isAnnexPoint,
        CancellationToken ct)
    {
        if (db.NdRegulationPoints.Local.Any(p => p.Id == pointId))
            return;

        var existing = await db.NdRegulationPoints.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == pointId, ct);
        if (existing != null)
        {
            if (existing.RegulationDocumentId != regulationDocumentId)
            {
                await db.NdRegulationPoints
                    .Where(p => p.Id == pointId)
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(p => p.RegulationDocumentId, regulationDocumentId),
                        ct);
            }
            return;
        }

        db.NdRegulationPoints.Add(new NdRegulationPoint
        {
            Id = pointId,
            RegulationDocumentId = regulationDocumentId,
            PointNumber = pointNumber,
            PointTitle = title,
            PointContent = content,
            PageReference = section,
            IsIntroductionPoint = isIntroductionPoint,
            IsAnnexPoint = isAnnexPoint,
        });
    }

    private static async Task<Guid?> FindContentMatchAsync(
        AppDbContext db,
        Guid regulationDocumentId,
        string pointNumber,
        string? title,
        string content,
        CancellationToken ct)
    {
        var candidates = await db.NdRegulationPoints.AsNoTracking()
            .Where(p => p.RegulationDocumentId == regulationDocumentId && p.PointNumber == pointNumber)
            .ToListAsync(ct);

        foreach (var candidate in candidates)
        {
            if (PointContentMatches(candidate, pointNumber, title, content, regulationDocumentId))
                return candidate.Id;
        }

        return null;
    }

    private static bool PointContentMatches(
        NdRegulationPoint existing,
        string pointNumber,
        string? title,
        string content,
        Guid regulationDocumentId)
    {
        if (existing.RegulationDocumentId != regulationDocumentId) return false;
        if (!string.Equals(existing.PointNumber.Trim(), pointNumber.Trim(), StringComparison.Ordinal))
            return false;
        if (!string.Equals(Normalize(existing.PointTitle), Normalize(title), StringComparison.Ordinal))
            return false;
        return string.Equals(
            Normalize(existing.PointContent),
            Normalize(content),
            StringComparison.Ordinal);
    }

    private static string Normalize(string? value) =>
        (value ?? "").Trim().Replace("\r\n", "\n");

    private static void ParseSnapshot(
        object? snapshot,
        out string pointNumber,
        out string? title,
        out string content,
        out string? section,
        out bool isIntroductionPoint,
        out bool isAnnexPoint)
    {
        pointNumber = "";
        title = null;
        content = "";
        section = null;
        isIntroductionPoint = false;
        isAnnexPoint = false;

        if (snapshot == null) return;

        JsonElement root;
        if (snapshot is JsonElement element)
            root = element;
        else if (snapshot is string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            using var doc = JsonDocument.Parse(raw);
            root = doc.RootElement.Clone();
        }
        else
        {
            var json = JsonSerializer.Serialize(snapshot);
            using var doc = JsonDocument.Parse(json);
            root = doc.RootElement.Clone();
        }

        pointNumber = ReadString(root, "pointNumber");
        title = ReadOptionalString(root, "pointTitle");
        content = ReadString(root, "pointContent");
        section = ReadOptionalString(root, "pageReference");
        isIntroductionPoint = ReadBool(root, "isIntroductionPoint");
        isAnnexPoint = ReadBool(root, "isAnnexPoint");
    }

    private static string? SerializeSnapshot(object? snapshot)
    {
        if (snapshot == null) return null;
        if (snapshot is string raw)
            return string.IsNullOrWhiteSpace(raw) ? null : raw;
        if (snapshot is JsonElement element)
            return element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : element.GetRawText();
        return JsonSerializer.Serialize(snapshot);
    }

    private static string ReadString(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value)) return "";
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();
    }

    private static string? ReadOptionalString(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static bool ReadBool(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.True;

    private static bool IsDepartmentOnlyOverlay(NdRegulationDocument doc) =>
        doc.StoredDocumentId.HasValue && string.IsNullOrWhiteSpace(doc.FilePath);

    /// <summary>Stable id for legacy gov points — includes title so duplicate point numbers stay unique.</summary>
    public static Guid LegacyPointId(Guid documentId, string pointNumber, string? title = null, bool isAnnex = false)
    {
        var key = $"{documentId:N}|{pointNumber.Trim().TrimEnd('.')}|{Normalize(title)}|{(isAnnex ? "annex" : "main")}";
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(key));
        return new Guid(hash);
    }
}
