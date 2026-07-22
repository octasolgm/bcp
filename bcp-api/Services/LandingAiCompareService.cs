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
        if (string.IsNullOrWhiteSpace(internalFileHash) && internalPdf is not { Length: > 0 })
            throw new InvalidOperationException("Internal document hash or PDF bytes required.");

        var hash = !string.IsNullOrWhiteSpace(internalFileHash)
            ? internalFileHash.Trim()
            : LandingAiCacheRepository.HashBuffer(internalPdf!);

        var markdown = await ResolveMarkdownFromPdfOrCacheAsync(internalPdf, internalFileName, hash, ct);
        return await ComparePointAsync(
            point,
            [new InternalDocPayload(hash, internalFileName, markdown, internalPdf)],
            forceRefresh,
            ct);
    }

    public async Task<string> ComparePointAsync(
        GovPoint point,
        IReadOnlyList<InternalDocPayload> internalDocs,
        bool forceRefresh,
        CancellationToken ct = default)
    {
        if (!client.IsConfigured)
            throw new InvalidOperationException(
                "Landing AI Phase 1 is not configured. Add LandingAi:ApiKey to appsettings.Development.json.");
        if (internalDocs.Count == 0)
            throw new InvalidOperationException("At least one internal document is required for compare.");

        var compositeHash = LandingAiCacheRepository.CompositeFileHash(
            internalDocs.Select(d => d.FileHash));
        var displayName = FormatDocLabel(internalDocs);
        var promptVersion = internalDocs.Count > 1 ? "v2-multi" : "v2";
        var compareKey = LandingAiCacheRepository.CompareCacheKey(compositeHash, point.PointId, promptVersion);

        if (!forceRefresh)
        {
            var cached = await cache.GetCompareCacheAsync(compareKey, ct);
            if (cached is { } hit)
            {
                var fromCache = LandingAiComparisonNormalizer.Normalize(hit, point.Text);
                fromCache = LandingAiComparisonNormalizer.Reapply(fromCache, point.Text);
                fromCache = ResolvePolicyPages(fromCache, internalDocs);
                logger.LogInformation("Landing AI compare cache hit for {Point}", point.PointId);
                return LandingAiComparisonFormatter.FormatMessage(point, displayName, fromCache);
            }
        }

        logger.LogInformation(
            "Landing AI compare starting for {Point} (docs={Count}, hash={Hash})",
            point.PointId,
            internalDocs.Count,
            compositeHash);

        var promptMarkdown = LandingAiComparePromptBuilder.Build(
            point,
            internalDocs.Select(d => (d.FileName, d.Markdown)).ToList());

        logger.LogInformation("Landing AI extract starting for {Point}", point.PointId);
        var extraction = await client.ExtractComparisonAsync(promptMarkdown, ct);
        var comparison = LandingAiComparisonNormalizer.Normalize(extraction, point.Text);
        await cache.SaveCompareCacheAsync(compareKey, extraction, _opts.ExtractModel, ct);

        comparison = ResolvePolicyPages(comparison, internalDocs);
        return LandingAiComparisonFormatter.FormatMessage(point, displayName, comparison);
    }

    private static ComplianceComparisonResult ResolvePolicyPages(
        ComplianceComparisonResult comparison,
        IReadOnlyList<InternalDocPayload> internalDocs)
    {
        if (string.IsNullOrWhiteSpace(comparison.OutputResponse)) return comparison;

        int? resolved = null;
        foreach (var doc in internalDocs)
        {
            resolved = PolicyPageResolver.Resolve(doc.Markdown, comparison.OutputResponse);
            if (resolved.HasValue) break;
        }

        if (resolved is > 0)
        {
            comparison.OutputResponse = PolicyPageResolver.RewriteCitationPage(
                comparison.OutputResponse,
                resolved.Value);
        }

        return comparison;
    }

    public async Task<string?> GetStoredParseAsync(string fileHash, CancellationToken ct = default)
    {
        var row = await cache.GetParseCacheAsync(fileHash, ct);
        return row?.Markdown;
    }

    private async Task<string> ResolveMarkdownFromPdfOrCacheAsync(
        byte[]? internalPdf,
        string internalFileName,
        string fileHash,
        CancellationToken ct)
    {
        var cached = await cache.GetParseCacheAsync(fileHash, ct);
        if (!string.IsNullOrWhiteSpace(cached?.Markdown))
            return cached.Markdown;

        if (internalPdf is not { Length: > 0 })
            throw new InvalidOperationException(
                "Internal markdown not found. Parse the document first or provide PDF bytes.");

        logger.LogInformation("Landing AI PDF parse starting ({File}, {Kb} KB)", internalFileName, internalPdf.Length / 1024);
        var markdown = await client.ParseDocumentAsync(internalPdf, internalFileName, ct);
        await cache.SaveParseCacheAsync(fileHash, internalFileName, markdown, _opts.ParseModel, ct);
        return markdown;
    }

    private static string FormatDocLabel(IReadOnlyList<InternalDocPayload> docs)
    {
        if (docs.Count == 0) return "internal.pdf";
        if (docs.Count == 1) return docs[0].FileName;
        return string.Join(" + ", docs.Select(d => d.FileName));
    }
}
