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

    public static string CompareCacheKey(string internalFileHash, string pointId, string promptVersion = "v2")
    {
        var revision = promptVersion == "v1" ? "v1" : "v2-semantic";
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
