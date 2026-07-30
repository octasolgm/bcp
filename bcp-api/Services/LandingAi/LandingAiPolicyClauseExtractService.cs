using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Reguliq.Api.Services.NewDashboard;

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

    /// <summary>Larger chunks = fewer Landing API calls (credits). Typical internal PDFs fit in 1–2 calls.</summary>
    public const int InternalSectionPagesPerChunk = 40;

    private static string ChunkSchemaKey(int chunkIndex) => $"{PolicyClausesSchemaKey}:chunk:{chunkIndex}";

    private static string ChunkCachePropertyKey(int chunkIndex) => chunkIndex.ToString();

    private readonly LandingAiOptions _opts = options.Value;

    public string ExtractModel => _opts.ExtractModel;

    /// <summary>Load merged clauses from full or per-chunk extract cache — no Landing API calls.</summary>
    public async Task<IReadOnlyList<PolicyClause>?> TryLoadCachedClausesAsync(
        string cacheKey,
        string? legacyContentHash = null,
        CancellationToken ct = default)
    {
        var key = cacheKey.Trim();
        var fromFull = await LoadMergedFromCacheKeyAsync(key, ct);
        if (fromFull != null)
            return fromFull;

        var legacy = (legacyContentHash ?? "").Trim();
        if (legacy.Length > 0 && !string.Equals(legacy, key, StringComparison.OrdinalIgnoreCase))
        {
            fromFull = await LoadMergedFromCacheKeyAsync(legacy, ct);
            if (fromFull != null)
                return fromFull;
        }

        return null;
    }

    private async Task<IReadOnlyList<PolicyClause>?> LoadMergedFromCacheKeyAsync(
        string cacheKey,
        CancellationToken ct)
    {
        var fullJson = await cache.GetExtractPointsJsonAsync(cacheKey, PolicyClausesSchemaKey, ct);
        if (!string.IsNullOrWhiteSpace(fullJson))
        {
            var fromClauses = PolicyClauseExtractNormalizer.DedupeClauseNumbers(
                PolicyClauseParser.ParseFromExtractJson(fullJson));
            if (fromClauses.Count > 0)
                return fromClauses;

            var fromEmbeddedChunks = AssembleClausesFromChunkCacheJson(fullJson);
            if (fromEmbeddedChunks.Count > 0)
            {
                logger.LogInformation(
                    "Assembled {Count} policy clauses from embedded chunk cache ({Prefix}…)",
                    fromEmbeddedChunks.Count,
                    cacheKey.Length >= 12 ? cacheKey[..12] : cacheKey);
                return PolicyClauseExtractNormalizer.DedupeClauseNumbers(fromEmbeddedChunks);
            }
        }

        var merged = new List<PolicyClause>();
        for (var i = 0; i < 32; i++)
        {
            var chunk = await TryLoadChunkFromCacheAsync(cacheKey, i, ct);
            if (chunk.Count == 0)
                break;
            merged.AddRange(chunk);
        }

        if (merged.Count == 0)
            return null;

        logger.LogInformation(
            "Assembled {Count} policy clauses from per-chunk extract cache ({Prefix}…)",
            merged.Count,
            cacheKey.Length >= 12 ? cacheKey[..12] : cacheKey);
        return PolicyClauseExtractNormalizer.DedupeClauseNumbers(merged);
    }

    public async Task<IReadOnlyList<PolicyClause>> ExtractFromMarkdownAsync(
        string fileHash,
        string fileName,
        string markdown,
        string? legacyContentHash = null,
        Func<ExtractionProgressUpdate, Task>? reportProgress = null,
        CancellationToken ct = default)
    {
        if (!client.IsConfigured)
            throw new InvalidOperationException("Landing AI is not configured.");

        if (string.IsNullOrWhiteSpace(markdown))
            throw new InvalidOperationException("Internal document has no parsed markdown.");

        var cacheKey = fileHash.Trim();
        var cachedClauses = await TryLoadCachedClausesAsync(cacheKey, legacyContentHash, ct);
        if (cachedClauses is { Count: > 0 })
        {
            if (reportProgress != null)
                await reportProgress(new ExtractionProgressUpdate("Using saved section extract…", 92));
            logger.LogInformation(
                "Loaded {Count} cached policy clauses for {File}",
                cachedClauses.Count,
                fileName);
            return await ResolveClausePagesAsync(markdown, cachedClauses.ToList(), ct);
        }

        logger.LogInformation(
            "Landing AI policy clause extract ({Model}, {File})",
            _opts.ExtractModel,
            fileName);

        var clauses = await ExtractClausesChunkedAsync(cacheKey, markdown, reportProgress, ct);
        if (clauses.Count == 0)
            throw new InvalidOperationException("No policy sections found in internal document.");

        clauses = await ResolveClausePagesAsync(markdown, clauses, ct);

        var json = SerializeClausesJson(clauses);

        await cache.SaveExtractPointsCacheAsync(
            cacheKey, PolicyClausesSchemaKey, json, _opts.ExtractModel, ct);

        return clauses;
    }

    private static string SerializeClausesJson(IReadOnlyList<PolicyClause> clauses) =>
        JsonSerializer.Serialize(new
        {
            clauses = clauses.Select(c => new
            {
                clause_no = c.ClauseNo,
                clause_text = c.ClauseText,
                source_page = c.SourcePage,
            }),
        });

    private async Task<List<PolicyClause>> ExtractClausesChunkedAsync(
        string cacheKey,
        string markdown,
        Func<ExtractionProgressUpdate, Task>? reportProgress,
        CancellationToken ct)
    {
        var chunks = GovMarkdownChunker.SplitForExtract(
            markdown,
            pagesPerChunk: InternalSectionPagesPerChunk);

        if (chunks.Count <= 1)
        {
            var fromCache = await TryLoadChunkFromCacheAsync(cacheKey, 0, ct);
            if (fromCache.Count > 0)
            {
                logger.LogInformation("Using cached policy clause chunk 1/1 (no Landing AI call)");
                if (reportProgress != null)
                    await reportProgress(new ExtractionProgressUpdate("Using saved section chunk 1/1…", 85));
                return fromCache;
            }

            if (reportProgress != null)
                await reportProgress(new ExtractionProgressUpdate("Extracting policy sections (1/1)…", 35));
            var extraction = await client.ExtractPolicyClausesAsync(markdown, ct);
            var single = PolicyClauseExtractNormalizer.DedupeClauseNumbers(
                PolicyClauseParser.ParseFromExtraction(extraction));
            await SaveChunkCacheAsync(cacheKey, 0, single, ct);
            return single;
        }

        var merged = new List<PolicyClause>();
        for (var i = 0; i < chunks.Count; i++)
        {
            var cachedChunk = await TryLoadChunkFromCacheAsync(cacheKey, i, ct);
            if (cachedChunk.Count > 0)
            {
                logger.LogInformation(
                    "Using cached policy clause chunk {Part}/{Total} (no Landing AI call)",
                    i + 1,
                    chunks.Count);
                if (reportProgress != null)
                {
                    var cachedPct = (int)Math.Round(((i + 1) / (double)chunks.Count) * 85) + 5;
                    await reportProgress(new ExtractionProgressUpdate(
                        $"Using saved section chunk {i + 1}/{chunks.Count}…",
                        Math.Min(cachedPct, 90)));
                }
                merged.AddRange(cachedChunk);
                continue;
            }

            if (reportProgress != null)
            {
                var startPct = (int)Math.Round((i / (double)chunks.Count) * 80) + 10;
                await reportProgress(new ExtractionProgressUpdate(
                    $"Extracting policy sections ({i + 1}/{chunks.Count})…",
                    Math.Min(startPct, 85)));
            }
            logger.LogInformation(
                "Policy clause extract part {Part}/{Total}",
                i + 1,
                chunks.Count);
            var extraction = await client.ExtractPolicyClausesAsync(chunks[i], ct);
            var part = PolicyClauseExtractNormalizer.DedupeClauseNumbers(
                PolicyClauseParser.ParseFromExtraction(extraction));
            await SaveChunkCacheAsync(cacheKey, i, part, ct);
            merged.AddRange(part);
        }

        return PolicyClauseExtractNormalizer.DedupeClauseNumbers(merged);
    }

    private async Task<List<PolicyClause>> TryLoadChunkFromCacheAsync(
        string cacheKey,
        int chunkIndex,
        CancellationToken ct)
    {
        var fullJson = await cache.GetExtractPointsJsonAsync(cacheKey, PolicyClausesSchemaKey, ct);
        if (!string.IsNullOrWhiteSpace(fullJson))
        {
            var embedded = ParseChunkFromCacheJson(fullJson, chunkIndex);
            if (embedded.Count > 0)
                return embedded;
        }

        var legacyJson = await cache.GetExtractPointsJsonAsync(cacheKey, ChunkSchemaKey(chunkIndex), ct);
        if (string.IsNullOrWhiteSpace(legacyJson))
            return [];
        return PolicyClauseExtractNormalizer.DedupeClauseNumbers(
            PolicyClauseParser.ParseFromExtractJson(legacyJson));
    }

    private async Task SaveChunkCacheAsync(
        string cacheKey,
        int chunkIndex,
        List<PolicyClause> clauses,
        CancellationToken ct)
    {
        if (clauses.Count == 0) return;

        var existingJson = await cache.GetExtractPointsJsonAsync(cacheKey, PolicyClausesSchemaKey, ct);
        var mergedJson = MergeChunkIntoCacheJson(existingJson, chunkIndex, clauses);
        await cache.SaveExtractPointsCacheAsync(
            cacheKey,
            PolicyClausesSchemaKey,
            mergedJson,
            _opts.ExtractModel,
            ct);
        logger.LogInformation(
            "Saved policy clause chunk {Chunk} to extract cache ({Count} clauses)",
            chunkIndex + 1,
            clauses.Count);
    }

    private static List<PolicyClause> ParseChunkFromCacheJson(string json, int chunkIndex)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("chunk_cache", out var chunkCache)
                || chunkCache.ValueKind != JsonValueKind.Object)
                return [];

            var key = ChunkCachePropertyKey(chunkIndex);
            if (!chunkCache.TryGetProperty(key, out var chunkNode))
                return [];

            return PolicyClauseExtractNormalizer.DedupeClauseNumbers(
                PolicyClauseParser.ParseFromExtraction(chunkNode));
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static List<PolicyClause> AssembleClausesFromChunkCacheJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("chunk_cache", out var chunkCache)
                || chunkCache.ValueKind != JsonValueKind.Object)
                return [];

            var merged = new List<PolicyClause>();
            foreach (var chunkNode in chunkCache.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                merged.AddRange(PolicyClauseParser.ParseFromExtraction(chunkNode.Value));

            return merged;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string MergeChunkIntoCacheJson(
        string? existingJson,
        int chunkIndex,
        IReadOnlyList<PolicyClause> clauses)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();

            var hasChunkCache = false;
            if (!string.IsNullOrWhiteSpace(existingJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(existingJson);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("clauses", out var clausesNode)
                        && clausesNode.ValueKind == JsonValueKind.Array
                        && clausesNode.GetArrayLength() > 0)
                    {
                        writer.WritePropertyName("clauses");
                        clausesNode.WriteTo(writer);
                    }

                    if (root.TryGetProperty("chunk_cache", out var chunkCache)
                        && chunkCache.ValueKind == JsonValueKind.Object)
                    {
                        writer.WritePropertyName("chunk_cache");
                        writer.WriteStartObject();
                        foreach (var prop in chunkCache.EnumerateObject())
                        {
                            if (prop.Name == ChunkCachePropertyKey(chunkIndex))
                                continue;
                            writer.WritePropertyName(prop.Name);
                            prop.Value.WriteTo(writer);
                        }
                        hasChunkCache = true;
                    }
                }
                catch (JsonException)
                {
                    // Replace invalid cache with fresh chunk payload.
                }
            }

            if (!hasChunkCache)
            {
                writer.WritePropertyName("chunk_cache");
                writer.WriteStartObject();
            }

            writer.WritePropertyName(ChunkCachePropertyKey(chunkIndex));
            writer.WriteStartObject();
            writer.WritePropertyName("clauses");
            writer.WriteStartArray();
            foreach (var clause in clauses)
            {
                writer.WriteStartObject();
                writer.WriteString("clause_no", clause.ClauseNo);
                writer.WriteString("clause_text", clause.ClauseText);
                writer.WriteNumber("source_page", clause.SourcePage);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
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
