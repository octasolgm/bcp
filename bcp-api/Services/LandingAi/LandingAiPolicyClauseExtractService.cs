using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Reguliq.Api.Services.LandingAi;

/// <summary>
/// Extract internal policy sections via Landing AI using Regul.ai EXTRACTION_TOOL_SCHEMA shape
/// (clause_no, clause_text, source_page). Cached per file hash — same as gov extract.
/// </summary>
public class LandingAiPolicyClauseExtractService(
    LandingAiHttpClient client,
    LandingAiCacheRepository cache,
    IOptions<LandingAiOptions> options,
    ILogger<LandingAiPolicyClauseExtractService> logger)
{
    public const string PolicyClausesSchemaKey = "policy_clauses_v1";

    /// <summary>Regul.ai INTERNAL_SECTION_PAGE_CHUNK_SIZE.</summary>
    public const int InternalSectionPagesPerChunk = 15;

    private readonly LandingAiOptions _opts = options.Value;

    public async Task<IReadOnlyList<PolicyClause>> ExtractFromMarkdownAsync(
        string fileHash,
        string fileName,
        string markdown,
        CancellationToken ct = default)
    {
        if (!client.IsConfigured)
            throw new InvalidOperationException("Landing AI is not configured.");

        if (string.IsNullOrWhiteSpace(markdown))
            throw new InvalidOperationException("Internal document has no parsed markdown.");

        var cacheKey = fileHash.Trim();
        var cachedJson = await cache.GetExtractPointsJsonAsync(cacheKey, PolicyClausesSchemaKey, ct);
        if (!string.IsNullOrWhiteSpace(cachedJson))
        {
            var cached = PolicyClauseExtractNormalizer.DedupeClauseNumbers(
                PolicyClauseParser.ParseFromExtractJson(cachedJson));
            logger.LogInformation(
                "Loaded {Count} cached policy clauses for {File}",
                cached.Count,
                fileName);
            return await ResolveClausePagesAsync(markdown, cached, ct);
        }

        logger.LogInformation(
            "Landing AI policy clause extract ({Model}, {File})",
            _opts.ExtractModel,
            fileName);

        var clauses = await ExtractClausesChunkedAsync(markdown, ct);
        if (clauses.Count == 0)
            throw new InvalidOperationException("No policy sections found in internal document.");

        clauses = await ResolveClausePagesAsync(markdown, clauses, ct);

        var json = JsonSerializer.Serialize(new
        {
            clauses = clauses.Select(c => new
            {
                clause_no = c.ClauseNo,
                clause_text = c.ClauseText,
                source_page = c.SourcePage,
            }),
        });

        await cache.SaveExtractPointsCacheAsync(
            cacheKey, PolicyClausesSchemaKey, json, _opts.ExtractModel, ct);

        return clauses;
    }

    private async Task<List<PolicyClause>> ExtractClausesChunkedAsync(string markdown, CancellationToken ct)
    {
        var chunks = GovMarkdownChunker.SplitForExtract(
            markdown,
            pagesPerChunk: InternalSectionPagesPerChunk);

        if (chunks.Count <= 1)
        {
            var extraction = await client.ExtractPolicyClausesAsync(markdown, ct);
            return PolicyClauseExtractNormalizer.DedupeClauseNumbers(
                PolicyClauseParser.ParseFromExtraction(extraction));
        }

        var merged = new List<PolicyClause>();
        for (var i = 0; i < chunks.Count; i++)
        {
            logger.LogInformation(
                "Policy clause extract part {Part}/{Total}",
                i + 1,
                chunks.Count);
            var extraction = await client.ExtractPolicyClausesAsync(chunks[i], ct);
            merged.AddRange(PolicyClauseParser.ParseFromExtraction(extraction));
        }

        return PolicyClauseExtractNormalizer.DedupeClauseNumbers(merged);
    }

    private Task<List<PolicyClause>> ResolveClausePagesAsync(
        string markdown,
        List<PolicyClause> clauses,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var totalPages = PolicyPageResolver.EstimatePageCount(markdown);
        var resolved = clauses.Select(c =>
        {
            if (c.SourcePage > 0) return c;
            var page = PolicyPageResolver.ResolveGovPointPage(
                markdown, c.ClauseNo, null, null, c.ClauseText, null, totalPages);
            return page is int p and > 0 ? c with { SourcePage = p } : c;
        }).ToList();
        return Task.FromResult(resolved);
    }
}
