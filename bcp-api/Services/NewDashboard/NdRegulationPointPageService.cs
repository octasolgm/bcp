using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Services.LandingAi;

namespace Reguliq.Api.Services.NewDashboard;

/// <summary>Resolve PDF pages for regulation points using cached parse markdown (no Landing AI).</summary>
public sealed class NdRegulationPointPageService(
    AppDbContext db,
    LandingAiCacheRepository landingCache)
{
    private readonly Dictionary<Guid, string?> _markdownByStoredId = new();

    public async Task<int?> ResolveForRegulationDocumentAsync(
        Guid regulationDocumentId,
        string pointNumber,
        string? title,
        string content,
        string? pageReference,
        CancellationToken ct = default)
    {
        var storedId = await ResolveStoredDocumentIdAsync(regulationDocumentId, ct);
        if (storedId is null) return null;

        var (section, pageHint) = ParsePointPageReference(pageReference);
        return await ResolveStoredPointPdfPageAsync(
            storedId.Value,
            pointNumber,
            section ?? pointNumber,
            title,
            content,
            pageHint,
            ct);
    }

    /// <summary>Add <c>pdfPage</c> to analysis point snapshot JSON when resolvable.</summary>
    public async Task<string> EnrichAnalysisPointSnapshotAsync(
        string? raw,
        Guid? analysisRegulationPointId = null,
        IReadOnlyList<Guid>? runRegulationDocumentIds = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "{}";

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(raw);
        }
        catch
        {
            return raw;
        }

        using (doc)
        {
            var root = doc.RootElement;
            Guid? regDocId = null;
            if (root.TryGetProperty("regulationDocumentId", out var rdProp)
                && Guid.TryParse(rdProp.GetString(), out var fromSnap))
            {
                regDocId = fromSnap;
            }

            if (regDocId is null)
            {
                Guid? snapRegPointId = null;
                if (root.TryGetProperty("regulationPointId", out var rpidProp)
                    && Guid.TryParse(rpidProp.GetString(), out var fromSnapRp))
                {
                    snapRegPointId = fromSnapRp;
                }

                regDocId = await ResolveRegulationDocumentIdFallbackAsync(
                    analysisRegulationPointId ?? snapRegPointId,
                    runRegulationDocumentIds,
                    ct);
            }

            if (regDocId is null) return raw;

            var pointNumber = ReadString(root, "pointNumber") ?? ReadString(root, "pointId");
            if (string.IsNullOrWhiteSpace(pointNumber)) return raw;

            var title = ReadOptionalString(root, "pointTitle");
            var content = ReadString(root, "pointContent") ?? "";
            var pageRef = ReadOptionalString(root, "pageReference");

            var pdfPage = await ResolveForRegulationDocumentAsync(
                regDocId.Value, pointNumber, title, content, pageRef, ct);
            if (pdfPage is null or <= 0) return raw;

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                var hasValidRegDocId = root.TryGetProperty("regulationDocumentId", out var rdEl)
                    && Guid.TryParse(rdEl.GetString(), out _);
                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.NameEquals("pdfPage"))
                        continue;
                    if (prop.NameEquals("regulationDocumentId") && !hasValidRegDocId)
                        continue;
                    prop.WriteTo(writer);
                }

                if (!hasValidRegDocId)
                    writer.WriteString("regulationDocumentId", regDocId.Value.ToString());

                writer.WriteNumber("pdfPage", pdfPage.Value);
                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }
    }

    private async Task<Guid?> ResolveRegulationDocumentIdFallbackAsync(
        Guid? regulationPointId,
        IReadOnlyList<Guid>? runRegulationDocumentIds,
        CancellationToken ct)
    {
        if (regulationPointId is Guid rpId)
        {
            var row = await db.NdRegulationPoints.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == rpId, ct);
            if (row?.RegulationDocumentId is Guid docId) return docId;
        }

        if (runRegulationDocumentIds is { Count: 1 })
            return runRegulationDocumentIds[0];

        return null;
    }

    private async Task<Guid?> ResolveStoredDocumentIdAsync(Guid regulationDocumentId, CancellationToken ct)
    {
        var ndDoc = await db.NdRegulationDocuments.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == regulationDocumentId, ct)
            ?? await db.NdRegulationDocuments.AsNoTracking()
                .FirstOrDefaultAsync(d => d.StoredDocumentId == regulationDocumentId, ct);

        if (ndDoc?.StoredDocumentId is Guid sid) return sid;
        if (ndDoc != null) return ndDoc.Id;

        var stored = await db.StoredDocuments.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == regulationDocumentId && d.DocKind == "regulation", ct);
        return stored?.Id;
    }

    private async Task<int?> ResolveStoredPointPdfPageAsync(
        Guid storedDocumentId,
        string pointId,
        string? section,
        string? title,
        string content,
        int? pageHint,
        CancellationToken ct)
    {
        if (!_markdownByStoredId.TryGetValue(storedDocumentId, out var markdown))
        {
            markdown = await LoadParsedMarkdownAsync(storedDocumentId, ct);
            _markdownByStoredId[storedDocumentId] = markdown;
        }

        var markerPages = PolicyPageResolver.EstimatePageCount(markdown);
        var storedPages = await LoadStoredDocumentPageCountAsync(storedDocumentId, ct);
        if ((storedPages is null or < 15) && markdown is { Length: > 40_000 })
            storedPages = Math.Clamp((int)Math.Round(markdown.Length / 4000.0), 20, 500);

        int? maxPages = markerPages is > 0 && storedPages is > 0
            ? Math.Max(markerPages.Value, storedPages.Value)
            : markerPages ?? storedPages;

        if (!string.IsNullOrWhiteSpace(markdown))
        {
            var hintForResolve = IsNumberedClausePoint(pointId) ? null : pageHint;
            var resolved = PolicyPageResolver.ResolveGovPointPage(
                markdown, pointId, section, title, content, hintForResolve, maxPages);
            var refined = PolicyPageResolver.RefinePageGuess(resolved, pointId, maxPages);
            if (refined is > 0) return refined;
        }

        if (maxPages is > 10)
        {
            var byPoint = PolicyPageResolver.EstimatePageFromPointNumber(pointId, maxPages.Value);
            if (byPoint is > 0) return byPoint;
        }

        var trustedHint = pageHint is > 0 && (maxPages is null or <= 0 || pageHint <= maxPages)
            ? pageHint
            : null;
        if (trustedHint == 1 && maxPages is > 10)
            trustedHint = null;
        return PolicyPageResolver.RefinePageGuess(
            ResolvePdfPage(section, trustedHint),
            pointId,
            maxPages);
    }

    private static bool IsNumberedClausePoint(string pointId) =>
        Regex.IsMatch(pointId.Trim().TrimEnd('.'), @"^\d+(\.\d+)+$");

    private async Task<string?> LoadParsedMarkdownAsync(Guid storedDocumentId, CancellationToken ct)
    {
        var stored = await db.StoredDocuments.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == storedDocumentId, ct);
        if (stored == null || string.IsNullOrWhiteSpace(stored.FileHash)) return null;

        var row = await landingCache.GetParseCacheAsync(stored.FileHash, ct);
        return row?.Markdown;
    }

    private async Task<int?> LoadStoredDocumentPageCountAsync(Guid storedDocumentId, CancellationToken ct)
    {
        var stored = await db.StoredDocuments.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == storedDocumentId, ct);
        if (stored == null) return null;
        if (stored.Pages is > 1) return stored.Pages;
        if (stored.SizeBytes is > 80_000)
            return Math.Clamp((int)Math.Round(stored.SizeBytes / 45000.0), 30, 500);
        return stored.Pages is > 0 ? stored.Pages : null;
    }

    private static int? ResolvePdfPage(string? pageReference, int? pageHint)
    {
        var fromRef = ParsePdfPageFromReference(pageReference);
        if (fromRef is > 0) return fromRef;
        return pageHint is > 0 ? pageHint : null;
    }

    private static (string? Section, int? PageHint) ParsePointPageReference(string? pageReference)
    {
        if (string.IsNullOrWhiteSpace(pageReference)) return (null, null);

        var hint = ParsePdfPageFromReference(pageReference);
        var section = pageReference.Trim();
        var sep = section.IndexOf(" · p.", StringComparison.OrdinalIgnoreCase);
        if (sep >= 0)
            section = section[..sep].Trim();
        else if (hint is > 0 && Regex.IsMatch(section, @"^p\.?\s*\d+$", RegexOptions.IgnoreCase))
            section = null;

        return (section, hint);
    }

    private static int? ParsePdfPageFromReference(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        var trimmed = reference.Trim();
        var match = Regex.Match(trimmed, @"(?:page|p\.?|pp\.?)\s*(\d+)", RegexOptions.IgnoreCase);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var fromLabel) && fromLabel > 0)
            return fromLabel;
        if (int.TryParse(trimmed, out var bare) && bare > 0) return bare;
        return null;
    }

    private static string? ReadString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var v)) return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
    }

    private static string? ReadOptionalString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
    }
}
