using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;

namespace Reguliq.Api.Services.Llm;

public class RegulWorkflowLlmSettingsService(
    AppDbContext db,
    IConfiguration config,
    IMemoryCache cache,
    ILogger<RegulWorkflowLlmSettingsService> logger)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(2);
    private const string CacheKey = "regul_workflow_llm_config";

    public async Task<DualVerifyLlmConfig> GetConfigAsync(CancellationToken ct = default)
    {
        if (cache.TryGetValue(CacheKey, out DualVerifyLlmConfig? cached) && cached != null)
            return cached;

        var row = await db.NdSystemSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == LlmProviderCatalog.RegulWorkflowSettingKey, ct);

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
                logger.LogWarning(ex, "Invalid Regul workflow LLM settings JSON — using defaults");
                normalized = LlmProviderCatalog.Normalize(null);
            }
        }

        cache.Set(CacheKey, normalized, CacheTtl);
        return normalized;
    }

    public async Task<DualVerifyLlmSettingsResponse> GetAdminViewAsync(CancellationToken ct = default)
    {
        var cfg = await GetConfigAsync(ct);
        var row = await db.NdSystemSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == LlmProviderCatalog.RegulWorkflowSettingKey, ct);

        var providers = LlmProviderCatalog.Providers.Values
            .Select(def => new LlmProviderStatusDto(
                def.Id,
                def.Label,
                def.Models,
                def.DefaultModel,
                IsApiKeyConfigured(def)))
            .ToList();

        return new DualVerifyLlmSettingsResponse(
            cfg.Provider,
            cfg.Model,
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
            .FirstOrDefaultAsync(s => s.Key == LlmProviderCatalog.RegulWorkflowSettingKey, ct);

        if (row == null)
        {
            row = new NdSystemSetting
            {
                Key = LlmProviderCatalog.RegulWorkflowSettingKey,
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
