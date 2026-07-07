using System.Text.Json;
using Microsoft.Extensions.Options;
using Reguliq.Api.Models;

namespace Reguliq.Api.Services.LandingAi;

/// <summary>Phase 1 dual-verify — Landing AI parse + compare (standalone, no NestJS :4000).</summary>
public class LandingAiCompareService(
    LandingAiHttpClient client,
    LandingAiCacheRepository cache,
    IOptions<LandingAiOptions> options,
    ILogger<LandingAiCompareService> logger)
{
    private readonly LandingAiOptions _opts = options.Value;
    public bool IsConfigured => client.IsConfigured;

    public async Task<string> ComparePointAsync(
        GovPoint point,
        string internalFileHash,
        string internalFileName,
        byte[]? internalPdf,
        bool forceRefresh,
        CancellationToken ct = default)
    {
        if (!client.IsConfigured)
            throw new InvalidOperationException(
                "Landing AI Phase 1 is not configured. Add LandingAi:ApiKey to appsettings.Development.json.");

        var resolved = await ResolveInternalMarkdownAsync(internalPdf, internalFileName, internalFileHash, ct);
        var compareKey = LandingAiCacheRepository.CompareCacheKey(resolved.FileHash, point.PointId);

        if (!forceRefresh)
        {
            var cached = await cache.GetCompareCacheAsync(compareKey, ct);
            if (cached is { } hit)
            {
                var fromCache = LandingAiComparisonNormalizer.Normalize(hit, point.Text);
                fromCache = LandingAiComparisonNormalizer.Reapply(fromCache, point.Text);
                logger.LogInformation("Landing AI compare cache hit for {Point}", point.PointId);
                return LandingAiComparisonFormatter.FormatMessage(point, resolved.FileName, fromCache);
            }
        }

        logger.LogInformation("Landing AI compare starting for {Point} (hash={Hash})", point.PointId, resolved.FileHash);

        var markdown = LandingAiComparePromptBuilder.Build(point, resolved.Markdown, resolved.FileName);
        logger.LogInformation("Landing AI extract starting for {Point}", point.PointId);
        var extraction = await client.ExtractComparisonAsync(markdown, ct);
        var comparison = LandingAiComparisonNormalizer.Normalize(extraction, point.Text);
        await cache.SaveCompareCacheAsync(compareKey, extraction, _opts.ExtractModel, ct);

        return LandingAiComparisonFormatter.FormatMessage(point, resolved.FileName, comparison);
    }

    public async Task<string?> GetStoredParseAsync(string fileHash, CancellationToken ct = default)
    {
        var row = await cache.GetParseCacheAsync(fileHash, ct);
        return row?.Markdown;
    }

    private async Task<ResolvedInternalDoc> ResolveInternalMarkdownAsync(
        byte[]? internalPdf,
        string internalFileName,
        string internalFileHash,
        CancellationToken ct)
    {
        if (internalPdf is { Length: > 0 })
        {
            var hash = LandingAiCacheRepository.HashBuffer(internalPdf);
            var cached = await cache.GetParseCacheAsync(hash, ct);
            if (!string.IsNullOrWhiteSpace(cached?.Markdown))
            {
                return new ResolvedInternalDoc(
                    cached.Markdown,
                    cached.FileName ?? internalFileName,
                    hash);
            }

            logger.LogInformation("Landing AI PDF parse starting ({File}, {Kb} KB)", internalFileName, internalPdf.Length / 1024);
            var markdown = await client.ParseDocumentAsync(internalPdf, internalFileName, ct);
        await cache.SaveParseCacheAsync(hash, internalFileName, markdown, _opts.ParseModel, ct);
            return new ResolvedInternalDoc(markdown, internalFileName, hash);
        }

        var hashHint = internalFileHash.Trim();
        if (!string.IsNullOrEmpty(hashHint))
        {
            var cached = await cache.GetParseCacheAsync(hashHint, ct);
            if (!string.IsNullOrWhiteSpace(cached?.Markdown))
            {
                return new ResolvedInternalDoc(
                    cached.Markdown,
                    string.IsNullOrWhiteSpace(internalFileName) ? cached.FileName : internalFileName,
                    hashHint);
            }
        }

        throw new InvalidOperationException(
            "Phase 1 needs internal PDF (upload in UI) or cached parse in Supabase (landing_ai_parse_cache).");
    }

    private sealed record ResolvedInternalDoc(string Markdown, string FileName, string FileHash);
}
