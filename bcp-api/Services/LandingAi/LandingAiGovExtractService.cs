using System.Text.Json;
using Microsoft.Extensions.Options;
using Reguliq.Api.Models;

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
    public const string GovSchemaKey = "gov_requirement_points";
    public const string BuiltinGovDocId = "gov-tfs-guidelines";
    public const string BuiltinGovFileHash =
        "c84713f9aacd18415680356aeae47bcacff9c17458b5595b575400b12fe8f2ff";

    private readonly LandingAiOptions _opts = options.Value;

    public async Task<GovExtractResponse> ExtractFromUploadAsync(
        byte[]? fileBytes,
        string fileName,
        string? markdownOverride,
        CancellationToken ct = default)
    {
        if (!client.IsConfigured)
            throw new InvalidOperationException(
                "Landing AI not configured. Set LandingAi:ApiKey in appsettings.");

        var fileHash = "markdown-only";
        var markdown = markdownOverride?.Trim() ?? "";

        if (fileBytes is { Length: > 0 })
            fileHash = LandingAiCacheRepository.HashBuffer(fileBytes);

        if (string.IsNullOrWhiteSpace(markdown) && fileBytes is { Length: > 0 })
        {
            logger.LogInformation("Landing AI parse for gov extract ({File}, {Kb} KB)", fileName, fileBytes.Length / 1024);
            markdown = await documentParse.ParseToMarkdownAsync(fileBytes, fileName, ct);
            await cache.SaveParseCacheAsync(fileHash, fileName, markdown, _opts.ParseModel, ct);
        }

        if (string.IsNullOrWhiteSpace(markdown))
            throw new InvalidOperationException("Upload a gov PDF or provide markdown.");

        var cachedJson = await cache.GetExtractPointsJsonAsync(fileHash, GovSchemaKey, ct);
        if (!string.IsNullOrWhiteSpace(cachedJson))
        {
            var cachedPoints = GovPointsParser.ParseFromExtractJson(cachedJson);
            cachedPoints = await ResolvePointPagesAsync(fileHash, cachedPoints, ct);
            govPoints.SetPoints(cachedPoints, "db-cache");
            return new GovExtractResponse(
                true, true, fileName, fileHash, GovSchemaKey,
                cachedPoints.Count, ToApiPoints(cachedPoints), 0, govPoints.Source);
        }

        logger.LogInformation("Landing AI gov extract ({Model})", _opts.ExtractModel);
        var extraction = await client.ExtractGovRequirementPointsAsync(markdown, ct);
        var points = GovPointsParser.ParseFromExtraction(extraction);
        if (points.Count == 0)
            throw new InvalidOperationException("No requirement points found in document.");

        points = await ResolvePointPagesAsync(fileHash, points, ct, markdown);

        var pointsJson = JsonSerializer.Serialize(new { points = ToApiPoints(points) });
        await cache.SaveExtractPointsCacheAsync(fileHash, GovSchemaKey, pointsJson, _opts.ExtractModel, ct);
        govPoints.SetPoints(points, "extract-live");

        return new GovExtractResponse(
            true, false, fileName, fileHash, GovSchemaKey,
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
            var points = GovPointsParser.ParseFromExtractJson(cachedJson);
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
            point_type = "mandatory",
        }).ToList();

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
            var text = p.TryGetProperty("text", out var pt) ? pt.GetString() : null;
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(text)) continue;
            int? pageHint = null;
            if (p.TryGetProperty("page_hint", out var ph) && ph.ValueKind == JsonValueKind.Number && ph.TryGetInt32(out var hint))
                pageHint = hint;
            list.Add(new GovPoint(
                id.Trim(),
                p.TryGetProperty("title", out var t) ? t.GetString() : null,
                text.Trim(),
                p.TryGetProperty("section", out var s) ? s.GetString() : null,
                pageHint));
        }
        return list;
    }
}
