using System.Text.Json;
using Microsoft.Extensions.Options;
using Reguliq.Api.Models;

namespace Reguliq.Api.Services.LandingAi;

/// <summary>Phase 1 dual-verify — Landing AI parse + compare (standalone, no NestJS :4000).</summary>
public class LandingAiCompareService(
    LandingAiHttpClient client,
    LandingAiDocumentParseService documentParse,
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
            ComparePromptVersion.V1,
            ct);
    }

    public async Task<string> ComparePointAsync(
        GovPoint point,
        IReadOnlyList<InternalDocPayload> internalDocs,
        bool forceRefresh,
        ComparePromptVersion promptVersion = ComparePromptVersion.V1,
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
        var cacheVersion = promptVersion.ToCacheKey(internalDocs.Count);
        var compareKey = LandingAiCacheRepository.CompareCacheKey(compositeHash, point.PointId, cacheVersion);

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

        logger.LogInformation(
            "Landing AI compare for {Point} using prompt {PromptVersion} ({PromptKey}) — fixed template, not admin-editable at runtime (see Admin \u2192 Analysis prompts for reference text only)",
            point.PointId,
            promptVersion,
            promptVersion == ComparePromptVersion.V3 ? "dual_verify_pass1_v3" : promptVersion.ToString());

        var promptMarkdown = LandingAiComparePromptBuilder.Build(
            point,
            internalDocs.Select(d => (d.FileName, d.Markdown)).ToList(),
            promptVersion);

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

        var lines = comparison.OutputResponse
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.Contains("No corresponding procedure found", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (lines.Count == 0) return comparison;

        var resolvedLines = new List<string>();
        foreach (var line in lines)
        {
            int? resolved = null;
            foreach (var doc in internalDocs)
            {
                resolved = PolicyPageResolver.Resolve(doc.Markdown, line);
                if (resolved.HasValue) break;
            }

            resolvedLines.Add(resolved is > 0
                ? PolicyPageResolver.RewriteCitationPage(line, resolved.Value)
                : line);
        }

        comparison.OutputResponse = string.Join('\n', resolvedLines);
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

        logger.LogInformation("Internal document parse for compare ({File}, {Kb} KB)", internalFileName, internalPdf.Length / 1024);
        var markdown = await documentParse.ParseToMarkdownAsync(internalPdf, internalFileName, ct);
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
