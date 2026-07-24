using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;

namespace Reguliq.Api.Services.Llm;

public class DualVerifyLlmSettingsService(
    AppDbContext db,
    IConfiguration config,
    IMemoryCache cache,
    ILogger<DualVerifyLlmSettingsService> logger)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(2);
    private const string CacheKey = "dual_verify_llm_config";

    public async Task<DualVerifyLlmConfig> GetConfigAsync(CancellationToken ct = default)
    {
        if (cache.TryGetValue(CacheKey, out DualVerifyLlmConfig? cached) && cached != null)
            return cached;

        var row = await db.NdSystemSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == LlmProviderCatalog.DualVerifySettingKey, ct);

        DualVerifyLlmConfig normalized;
        if (row == null)
        {
            normalized = LlmProviderCatalog.Normalize(null);
        }
        else
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<DualVerifyLlmConfig>(
                    row.ValueJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                normalized = LlmProviderCatalog.Normalize(parsed);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Invalid dual verify LLM settings JSON — using defaults");
                normalized = LlmProviderCatalog.Normalize(null);
            }
        }

        cache.Set(CacheKey, normalized, CacheTtl);
        return normalized;
    }

    public async Task<DualVerifyLlmSettingsResponse> GetAdminViewAsync(CancellationToken ct = default)
    {
        var config = await GetConfigAsync(ct);
        var row = await db.NdSystemSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == LlmProviderCatalog.DualVerifySettingKey, ct);

        var providers = LlmProviderCatalog.Providers.Values
            .Select(def => new LlmProviderStatusDto(
                def.Id,
                def.Label,
                def.Models,
                def.DefaultModel,
                IsApiKeyConfigured(def)))
            .ToList();

        return new DualVerifyLlmSettingsResponse(
            config.Provider,
            config.Model,
            providers,
            row?.UpdatedAt,
            row?.UpdatedBy);
    }

    public async Task<DualVerifyLlmSettingsResponse> SaveAsync(
        DualVerifyLlmConfig input,
        Guid updatedBy,
        CancellationToken ct = default)
    {
        var normalized = LlmProviderCatalog.Normalize(input);
        if (!IsApiKeyConfigured(LlmProviderCatalog.Get(normalized.Provider)))
        {
            throw new InvalidOperationException(
                $"API key for {normalized.Provider} is not configured on the server.");
        }

        var json = JsonSerializer.Serialize(normalized);
        var row = await db.NdSystemSettings
            .FirstOrDefaultAsync(s => s.Key == LlmProviderCatalog.DualVerifySettingKey, ct);

        if (row == null)
        {
            row = new NdSystemSetting
            {
                Key = LlmProviderCatalog.DualVerifySettingKey,
                ValueJson = json,
                UpdatedAt = DateTimeOffset.UtcNow,
                UpdatedBy = updatedBy,
            };
            db.NdSystemSettings.Add(row);
        }
        else
        {
            row.ValueJson = json;
            row.UpdatedAt = DateTimeOffset.UtcNow;
            row.UpdatedBy = updatedBy;
        }

        await db.SaveChangesAsync(ct);
        cache.Remove(CacheKey);
        return await GetAdminViewAsync(ct);
    }

    private bool IsApiKeyConfigured(LlmProviderDefinition def)
    {
        var fromConfig = config[def.ConfigKeyPath];
        if (!string.IsNullOrWhiteSpace(fromConfig)) return true;
        var fromEnv = Environment.GetEnvironmentVariable(def.EnvVarName);
        return !string.IsNullOrWhiteSpace(fromEnv);
    }
}

public record DualVerifyLlmSettingsResponse(
    string Provider,
    string Model,
    IReadOnlyList<LlmProviderStatusDto> Providers,
    DateTimeOffset? UpdatedAt,
    Guid? UpdatedBy);

public record LlmProviderStatusDto(
    string Id,
    string Label,
    string[] Models,
    string DefaultModel,
    bool ApiKeyConfigured);
