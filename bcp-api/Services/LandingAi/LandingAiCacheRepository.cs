using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;

namespace Reguliq.Api.Services.LandingAi;

/// <summary>Reads Landing AI parse/compare caches from Supabase PostgreSQL.</summary>
public class LandingAiCacheRepository(AppDbContext db, ILogger<LandingAiCacheRepository> logger)
{
    public async Task<ParseCacheRow?> GetParseCacheAsync(string fileHash, CancellationToken ct = default)
    {
        try
        {
            return await db.Database
                .SqlQueryRaw<ParseCacheRow>(
                    """
                    SELECT markdown AS "Markdown", file_name AS "FileName"
                    FROM landing_ai_parse_cache
                    WHERE file_hash = {0}
                    LIMIT 1
                    """,
                    fileHash)
                .FirstOrDefaultAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Parse cache miss for {Hash}", fileHash);
            return null;
        }
    }

    /// <summary>
    /// Per-document cache key first; if missing, reuse legacy content-hash row and copy to the doc key (no API call).
    /// </summary>
    public async Task<ParseCacheRow?> ResolveParseCacheAsync(
        string cacheKey,
        string? legacyContentHash,
        string parseModel,
        CancellationToken ct = default)
    {
        var row = await GetParseCacheAsync(cacheKey, ct);
        if (!string.IsNullOrWhiteSpace(row?.Markdown))
            return row;

        var legacyHash = (legacyContentHash ?? "").Trim();
        if (legacyHash.Length == 0
            || string.Equals(legacyHash, cacheKey, StringComparison.OrdinalIgnoreCase))
            return null;

        var legacy = await GetParseCacheAsync(legacyHash, ct);
        if (string.IsNullOrWhiteSpace(legacy?.Markdown))
            return null;

        logger.LogInformation(
            "Reusing legacy parse cache (content hash) under per-document key {Prefix}…",
            cacheKey.Length >= 12 ? cacheKey[..12] : cacheKey);
        await SaveParseCacheAsync(cacheKey, legacy.FileName, legacy.Markdown, parseModel, ct);
        return legacy;
    }

    public async Task SaveParseCacheAsync(
        string fileHash,
        string fileName,
        string markdown,
        string parseModel,
        CancellationToken ct = default)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO landing_ai_parse_cache (file_hash, file_name, markdown, parse_model, updated_at)
                VALUES ({0}, {1}, {2}, {3}, NOW())
                ON CONFLICT (file_hash) DO UPDATE SET
                  file_name = EXCLUDED.file_name,
                  markdown = EXCLUDED.markdown,
                  parse_model = EXCLUDED.parse_model,
                  updated_at = NOW()
                """,
                fileHash, fileName, markdown, parseModel);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not save parse cache for {Hash}", fileHash);
        }
    }

    public async Task<JsonElement?> GetCompareCacheAsync(string compareKey, CancellationToken ct = default)
    {
        try
        {
            var row = await db.Database
                .SqlQueryRaw<ExtractCacheRow>(
                    """
                    SELECT points_json::text AS "PointsJson"
                    FROM landing_ai_extract_cache
                    WHERE file_hash = {0} AND schema_key = 'compliance_comparison'
                    LIMIT 1
                    """,
                    compareKey)
                .FirstOrDefaultAsync(ct);

            if (row is null || string.IsNullOrWhiteSpace(row.PointsJson))
                return null;

            using var doc = JsonDocument.Parse(row.PointsJson);
            return doc.RootElement.Clone();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Compare cache miss for {Key}", compareKey);
            return null;
        }
    }

    public async Task SaveCompareCacheAsync(
        string compareKey,
        JsonElement comparison,
        string extractModel,
        CancellationToken ct = default)
    {
        try
        {
            var json = comparison.GetRawText();
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO landing_ai_extract_cache (file_hash, schema_key, points_json, extract_model, created_at)
                VALUES ({0}, 'compliance_comparison', {1}::jsonb, {2}, NOW())
                ON CONFLICT (file_hash, schema_key) DO UPDATE SET
                  points_json = EXCLUDED.points_json,
                  extract_model = EXCLUDED.extract_model,
                  created_at = NOW()
                """,
                compareKey, json, extractModel);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not save compare cache for {Key}", compareKey);
        }
    }

    public async Task<string?> GetExtractPointsJsonAsync(
        string fileHash,
        string schemaKey,
        CancellationToken ct = default)
    {
        try
        {
            var row = await db.Database
                .SqlQueryRaw<ExtractCacheRow>(
                    """
                    SELECT points_json::text AS "PointsJson"
                    FROM landing_ai_extract_cache
                    WHERE file_hash = {0} AND schema_key = {1}
                    LIMIT 1
                    """,
                    fileHash, schemaKey)
                .FirstOrDefaultAsync(ct);
            return row?.PointsJson;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Gov extract cache miss for {Hash}", fileHash);
            return null;
        }
    }

    /// <summary>
    /// Per-document cache key first; if missing, reuse legacy content-hash extract and copy to the doc key (no API call).
    /// </summary>
    public async Task<string?> ResolveExtractPointsJsonAsync(
        string cacheKey,
        string? legacyContentHash,
        string schemaKey,
        string extractModel,
        CancellationToken ct = default)
    {
        var json = await GetExtractPointsJsonAsync(cacheKey, schemaKey, ct);
        if (!string.IsNullOrWhiteSpace(json))
            return json;

        var legacyHash = (legacyContentHash ?? "").Trim();
        if (legacyHash.Length == 0
            || string.Equals(legacyHash, cacheKey, StringComparison.OrdinalIgnoreCase))
            return null;

        json = await GetExtractPointsJsonAsync(legacyHash, schemaKey, ct);
        if (string.IsNullOrWhiteSpace(json))
            return null;

        logger.LogInformation(
            "Reusing legacy {Schema} extract cache under per-document key {Prefix}…",
            schemaKey,
            cacheKey.Length >= 12 ? cacheKey[..12] : cacheKey);
        await SaveExtractPointsCacheAsync(cacheKey, schemaKey, json, extractModel, ct);
        return json;
    }

    public async Task SaveExtractPointsCacheAsync(
        string fileHash,
        string schemaKey,
        string pointsJson,
        string extractModel,
        CancellationToken ct = default)
    {
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO landing_ai_extract_cache (file_hash, schema_key, points_json, extract_model, created_at)
            VALUES ({0}, {1}, {2}::jsonb, {3}, NOW())
            ON CONFLICT (file_hash, schema_key) DO UPDATE SET
              points_json = EXCLUDED.points_json,
              extract_model = EXCLUDED.extract_model,
              created_at = NOW()
            """,
            fileHash, schemaKey, pointsJson, extractModel);
        logger.LogInformation(
            "Saved gov extract cache ({Schema}) for {Hash} ({Bytes} bytes)",
            schemaKey, fileHash, pointsJson.Length);
    }

    public static string HashBuffer(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static string CompareCacheKey(string internalFileHash, string pointId, string promptVersion = "v1")
    {
        var revision = promptVersion switch
        {
            "v1" => "compare-prompt-v1",
            "v2-multi" => "compare-prompt-v2-multi",
            "v2" => "compare-prompt-v2",
            _ => $"compare-prompt-{promptVersion}",
        };
        var input = $"{internalFileHash}:{pointId}:{revision}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
    }

    public static string CompositeFileHash(IEnumerable<string> fileHashes)
    {
        var sorted = fileHashes
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Select(h => h.Trim())
            .OrderBy(h => h, StringComparer.Ordinal)
            .ToArray();
        if (sorted.Length == 0) return "";
        if (sorted.Length == 1) return sorted[0];
        return HashBuffer(Encoding.UTF8.GetBytes(string.Join("|", sorted)));
    }
}
