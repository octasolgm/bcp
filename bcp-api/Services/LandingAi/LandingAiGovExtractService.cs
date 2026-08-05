using System.Text.Json;
using Microsoft.Extensions.Options;
using Reguliq.Api.Models;
using Reguliq.Api.Services.NewDashboard;

namespace Reguliq.Api.Services.LandingAi;

/// <summary>Extract numbered gov requirement points via Landing AI (parse + schema extract).</summary>
public class LandingAiGovExtractService(
    LandingAiHttpClient client,
    LandingAiDocumentParseService documentParse,
    LandingAiCacheRepository cache,
    GovPointsService govPoints,
    IOptions<LandingAiOptions> options,
    ILogger<LandingAiGovExtractService> logger)
{
    public const string GovSchemaKey = "gov_requirement_points_v3";
    public const string BuiltinGovDocId = "gov-tfs-guidelines";
    public const string BuiltinGovFileHash =
        "c84713f9aacd18415680356aeae47bcacff9c17458b5595b575400b12fe8f2ff";

    private readonly LandingAiOptions _opts = options.Value;

    public Task<GovExtractResponse> ExtractFromUploadAsync(
        byte[]? fileBytes,
        string fileName,
        string? markdownOverride,
        CancellationToken ct = default)
        => ExtractFromUploadAsync(fileBytes, fileName, markdownOverride, reportProgress: null, ct);

    public Task<GovExtractResponse> ExtractFromUploadAsync(
        byte[]? fileBytes,
        string fileName,
        string? markdownOverride,
        Func<ExtractionProgressUpdate, Task>? reportProgress,
        CancellationToken ct = default)
        => ExtractFromUploadAsync(fileBytes, fileName, markdownOverride, reportProgress, parseCheckpoint: null, cacheKeyOverride: null, ct);

    public async Task<GovExtractResponse> ExtractFromUploadAsync(
        byte[]? fileBytes,
        string fileName,
        string? markdownOverride,
        Func<ExtractionProgressUpdate, Task>? reportProgress,
        RegulationParseCheckpoint? parseCheckpoint,
        string? cacheKeyOverride = null,
        CancellationToken ct = default)
    {
        if (!client.IsConfigured)
            throw new InvalidOperationException(
                "Landing AI not configured. Set LandingAi:ApiKey in appsettings.");

        var contentHash = "markdown-only";
        var markdown = markdownOverride?.Trim() ?? "";

        if (fileBytes is { Length: > 0 })
            contentHash = LandingAiCacheRepository.HashBuffer(fileBytes);

        var cacheKey = string.IsNullOrWhiteSpace(cacheKeyOverride)
            ? contentHash
            : cacheKeyOverride.Trim();

        if (string.IsNullOrWhiteSpace(markdown) && fileBytes is { Length: > 0 })
        {
            var cachedParse = await cache.GetParseCacheAsync(cacheKey, ct);
            if (!string.IsNullOrWhiteSpace(cachedParse?.Markdown)
                && IsParseCacheComplete(cachedParse.Markdown, fileBytes, fileName))
            {
                logger.LogInformation(
                    "Using cached Landing AI parse for gov extract ({File}, cache {CachePrefix}…)",
                    fileName,
                    cacheKey.Length >= 12 ? cacheKey[..12] : cacheKey);
                if (reportProgress != null)
                    await reportProgress(new ExtractionProgressUpdate("Using cached document parse…", 58));
                markdown = cachedParse!.Markdown;
            }
            else
            {
                logger.LogInformation("Landing AI parse for gov extract ({File}, {Kb} KB)", fileName, fileBytes.Length / 1024);
                markdown = await documentParse.ParseToMarkdownAsync(
                    fileBytes, fileName, reportProgress, parseCheckpoint, ct);
                await cache.SaveParseCacheAsync(cacheKey, fileName, markdown, _opts.ParseModel, ct);
            }
        }

        if (string.IsNullOrWhiteSpace(markdown))
            throw new InvalidOperationException("Upload a gov PDF or provide markdown.");

        var cachedJson = await cache.GetExtractPointsJsonAsync(cacheKey, GovSchemaKey, ct);
        if (!string.IsNullOrWhiteSpace(cachedJson))
        {
            if (reportProgress != null)
                await reportProgress(new ExtractionProgressUpdate("Loading cached regulation points…", 85));
            var cachedPoints = GovPointExtractNormalizer.DedupeAndFilter(
                GovPointsParser.ParseFromExtractJson(cachedJson));
            cachedPoints = GovPointMarkdownRecovery.MergeMissing(cachedPoints, markdown);
            cachedPoints = await ResolvePointPagesAsync(cacheKey, cachedPoints, ct);
            govPoints.SetPoints(cachedPoints, "db-cache");
            return new GovExtractResponse(
                true, true, fileName, contentHash, GovSchemaKey,
                cachedPoints.Count, ToApiPoints(cachedPoints), 0, govPoints.Source);
        }

        logger.LogInformation("Landing AI gov extract ({Model})", _opts.ExtractModel);
        if (reportProgress != null)
            await reportProgress(new ExtractionProgressUpdate("Extracting regulation points…", 72));
        var points = await ExtractPointsChunkedAsync(markdown, reportProgress, ct);
        if (points.Count == 0)
            throw new InvalidOperationException("No requirement points found in document.");

        points = GovPointMarkdownRecovery.MergeMissing(points, markdown);
        points = await ResolvePointPagesAsync(cacheKey, points, ct, markdown);

        var pointsJson = JsonSerializer.Serialize(new { points = ToApiPoints(points) });
        await cache.SaveExtractPointsCacheAsync(cacheKey, GovSchemaKey, pointsJson, _opts.ExtractModel, ct);
        govPoints.SetPoints(points, "extract-live");

        return new GovExtractResponse(
            true, false, fileName, contentHash, GovSchemaKey,
            points.Count, ToApiPoints(points), null, govPoints.Source);
    }

    /// <summary>Load gov points from Supabase extract cache, else embedded seed.</summary>
    public async Task<GovLoadResponse> LoadFromDatabaseOrSeedAsync(
        string docId = BuiltinGovDocId,
        CancellationToken ct = default)
    {
        var fileHash = docId == BuiltinGovDocId ? BuiltinGovFileHash : docId;
        var cachedJson = await cache.GetExtractPointsJsonAsync(fileHash, GovSchemaKey, ct);
        if (!string.IsNullOrWhiteSpace(cachedJson))
        {
            var points = GovPointExtractNormalizer.DedupeAndFilter(
                GovPointsParser.ParseFromExtractJson(cachedJson));
            points = await ResolvePointPagesAsync(fileHash, points, ct);
            govPoints.SetPoints(points, "db-cache");
            logger.LogInformation("Loaded {Count} gov points from DB extract cache", points.Count);
            return new GovLoadResponse(true, "db-cache", points.Count, govPoints.Source);
        }

        govPoints.ReloadFromSeed();
        var count = govPoints.GetAllPoints().Count;
        logger.LogInformation("Loaded {Count} gov points from embedded seed", count);
        return new GovLoadResponse(true, "seed", count, govPoints.Source);
    }

    private static IReadOnlyList<object> ToApiPoints(IReadOnlyList<GovPoint> points) =>
        points.Select(p => new
        {
            point_id = p.PointId,
            title = p.Title,
            text = p.Text,
            section = p.Section,
            page_hint = p.PageHint is > 0 ? p.PageHint : 0,
            point_type = string.IsNullOrWhiteSpace(p.PointType) ? "mandatory" : p.PointType,
        }).ToList();

    private async Task<List<GovPoint>> ExtractPointsChunkedAsync(
        string markdown,
        Func<ExtractionProgressUpdate, Task>? reportProgress,
        CancellationToken ct)
    {
        var chunks = GovMarkdownChunker.SplitForExtract(markdown);
        if (chunks.Count <= 1)
        {
            var extraction = await client.ExtractGovRequirementPointsAsync(markdown, ct);
            return GovPointExtractNormalizer.DedupeAndFilter(GovPointsParser.ParseFromExtraction(extraction));
        }

        var merged = new List<GovPoint>();
        for (var i = 0; i < chunks.Count; i++)
        {
            if (reportProgress != null)
            {
                var pct = 72 + (int)Math.Round((i + 1) / (double)chunks.Count * 12);
                await reportProgress(new ExtractionProgressUpdate(
                    $"Extracting regulation points (part {i + 1}/{chunks.Count})…",
                    pct));
            }

            var extraction = await client.ExtractGovRequirementPointsAsync(chunks[i], ct);
            merged.AddRange(GovPointsParser.ParseFromExtraction(extraction));
        }

        return GovPointExtractNormalizer.DedupeAndFilter(merged);
    }

    private static bool IsParseCacheComplete(string markdown, byte[] fileBytes, string fileName)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return false;

        if (LandingAiDocumentFormats.IsPdf(fileName, fileBytes))
        {
            try
            {
                var pdfPages = LandingAiDocumentParseService.GetPdfPageCount(fileBytes);
                if (pdfPages <= 1) return markdown.Length > 500;
                var markerPages = PolicyPageResolver.EstimatePageCount(markdown);
                if (markerPages >= pdfPages) return true;
                if (markerPages >= (int)Math.Round(pdfPages * 0.85)) return true;
                return markdown.Length >= pdfPages * 600;
            }
            catch
            {
                return markdown.Length > 80_000;
            }
        }

        return markdown.Length > 2000;
    }

    private async Task<List<GovPoint>> ResolvePointPagesAsync(
        string fileHash,
        List<GovPoint> points,
        CancellationToken ct,
        string? markdownOverride = null)
    {
        var markdown = markdownOverride;
        if (string.IsNullOrWhiteSpace(markdown))
        {
            var row = await cache.GetParseCacheAsync(fileHash, ct);
            markdown = row?.Markdown;
        }

        if (string.IsNullOrWhiteSpace(markdown)) return points;

        return points.Select(p =>
        {
            var resolved = PolicyPageResolver.ResolveGovPointPage(
                markdown, p.PointId, p.Section, p.Title, p.Text, p.PageHint,
                PolicyPageResolver.EstimatePageCount(markdown));
            return resolved is > 0 ? p with { PageHint = resolved } : p;
        }).ToList();
    }
}

public sealed record GovExtractResponse(
    bool Success,
    bool Cached,
    string FileName,
    string FileHash,
    string SchemaKey,
    int PointCount,
    IReadOnlyList<object> Points,
    int? CreditUsage,
    string Source);

public sealed record GovLoadResponse(bool Success, string Source, int PointCount, string ActiveSource);

public static class GovPointsParser
{
    public static List<GovPoint> ParseFromExtractJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ParseFromExtraction(doc.RootElement);
    }

    public static List<GovPoint> ParseFromExtraction(JsonElement extraction)
    {
        var list = new List<GovPoint>();
        if (!extraction.TryGetProperty("points", out var points) || points.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var p in points.EnumerateArray())
        {
            var id = p.TryGetProperty("point_id", out var pid) ? pid.GetString() : null;
            var text = p.TryGetProperty("text", out var textEl) ? textEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(text)) continue;
            int? pageHint = null;
            if (p.TryGetProperty("page_hint", out var ph) && ph.ValueKind == JsonValueKind.Number && ph.TryGetInt32(out var hint))
                pageHint = hint;
            string? pointType = null;
            if (p.TryGetProperty("point_type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String)
                pointType = typeEl.GetString();
            list.Add(new GovPoint(
                id.Trim(),
                p.TryGetProperty("title", out var t) ? t.GetString() : null,
                text.Trim(),
                p.TryGetProperty("section", out var s) ? s.GetString() : null,
                pageHint,
                pointType));
        }
        return list;
    }
}
