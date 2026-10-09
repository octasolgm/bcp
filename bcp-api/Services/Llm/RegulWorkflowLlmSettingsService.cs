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

    private const string RetrievalCacheSettingKey = "regul_retrieval_prompt_cache";
    private const string RetrievalCacheMemoKey = "regul_retrieval_prompt_cache_enabled";

    /// <summary>Whether the hybrid (V5) engine marks its per-clause retrieval context for Anthropic
    /// prompt caching. Off by default: every clause retrieves a different set of chunks, so the
    /// cache is written (1.25x input price) on every call and never read. Full-markdown engines are
    /// unaffected — their context is identical across clauses, so caching genuinely pays there.
    /// Flip on from Admin settings if that ever changes (e.g. a shared per-run context).</summary>
    public async Task<bool> IsRetrievalPromptCacheEnabledAsync(CancellationToken ct = default)
    {
        if (cache.TryGetValue(RetrievalCacheMemoKey, out bool cached)) return cached;
        var row = await db.NdSystemSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == RetrievalCacheSettingKey, ct);
        var enabled = row != null && bool.TryParse(row.ValueJson, out var v) && v;
        cache.Set(RetrievalCacheMemoKey, enabled, CacheTtl);
        return enabled;
    }

    public async Task<bool> SetRetrievalPromptCacheEnabledAsync(bool enabled, Guid updatedBy, CancellationToken ct = default)
    {
        var row = await db.NdSystemSettings.FirstOrDefaultAsync(s => s.Key == RetrievalCacheSettingKey, ct);
        var json = enabled ? "true" : "false";
        if (row == null)
        {
            db.NdSystemSettings.Add(new NdSystemSetting
            {
                Key = RetrievalCacheSettingKey,
                ValueJson = json,
                UpdatedAt = DateTimeOffset.UtcNow,
                UpdatedBy = updatedBy,
            });
        }
        else
        {
            row.ValueJson = json;
            row.UpdatedAt = DateTimeOffset.UtcNow;
            row.UpdatedBy = updatedBy;
        }
        await db.SaveChangesAsync(ct);
        cache.Remove(RetrievalCacheMemoKey);
        return enabled;
    }

    private const string EmbeddingProviderSettingKey = "regul_embedding_provider";
    private const string EmbeddingProviderMemoKey = "regul_embedding_provider_current";
    public const string EmbeddingProviderLocal = "local";
    public const string EmbeddingProviderAzureOpenAi = "azure-openai";

    /// <summary>
    /// Embedding model for the search passages of pipeline v4+: the admin choice (Admin > Analysis prompts), else
    /// the RegulRetrieval:EmbeddingProvider config value, else local. Always "local" or "azure-openai".
    /// </summary>
    public async Task<string> GetEmbeddingProviderAsync(CancellationToken ct = default)
    {
        if (cache.TryGetValue(EmbeddingProviderMemoKey, out string? cached) && cached != null) return cached;
        var row = await db.NdSystemSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == EmbeddingProviderSettingKey, ct);
        var provider = NormalizeEmbeddingProvider(row?.ValueJson)
            ?? NormalizeEmbeddingProvider(config["RegulRetrieval:EmbeddingProvider"])
            ?? EmbeddingProviderLocal;
        cache.Set(EmbeddingProviderMemoKey, provider, CacheTtl);
        return provider;
    }

    public async Task<string> SetEmbeddingProviderAsync(string provider, Guid updatedBy, CancellationToken ct = default)
    {
        var value = NormalizeEmbeddingProvider(provider)
            ?? throw new InvalidOperationException($"Unknown embedding provider '{provider}'.");
        var row = await db.NdSystemSettings.FirstOrDefaultAsync(s => s.Key == EmbeddingProviderSettingKey, ct);
        if (row == null)
        {
            db.NdSystemSettings.Add(new NdSystemSetting
            {
                Key = EmbeddingProviderSettingKey,
                ValueJson = value,
                UpdatedAt = DateTimeOffset.UtcNow,
                UpdatedBy = updatedBy,
            });
        }
        else
        {
            row.ValueJson = value;
            row.UpdatedAt = DateTimeOffset.UtcNow;
            row.UpdatedBy = updatedBy;
        }
        await db.SaveChangesAsync(ct);
        cache.Remove(EmbeddingProviderMemoKey);
        return value;
    }

    public static string? NormalizeEmbeddingProvider(string? value) =>
        (value ?? "").Trim().Trim('"').ToLowerInvariant() switch
        {
            EmbeddingProviderLocal => EmbeddingProviderLocal,
            EmbeddingProviderAzureOpenAi => EmbeddingProviderAzureOpenAi,
            _ => null,
        };

    private const string GapCheckSkipLowSettingKey = "regul_gap_check_skip_low";
    private const string GapCheckSkipLowMemoKey = "regul_gap_check_skip_low_enabled";

    /// <summary>Pipeline v5+: skip the gap re-check (one AI call per gap) for gaps the judgment rated "Materiality:
    /// low". On by default (no stored row = on); off = every gap is re-checked, as before.</summary>
    public async Task<bool> IsGapCheckSkipLowEnabledAsync(CancellationToken ct = default)
    {
        if (cache.TryGetValue(GapCheckSkipLowMemoKey, out bool cached)) return cached;
        var row = await db.NdSystemSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == GapCheckSkipLowSettingKey, ct);
        var enabled = row == null || !bool.TryParse(row.ValueJson, out var v) || v;
        cache.Set(GapCheckSkipLowMemoKey, enabled, CacheTtl);
        return enabled;
    }

    public async Task<bool> SetGapCheckSkipLowAsync(bool enabled, Guid updatedBy, CancellationToken ct = default)
    {
        var row = await db.NdSystemSettings.FirstOrDefaultAsync(s => s.Key == GapCheckSkipLowSettingKey, ct);
        var json = enabled ? "true" : "false";
        if (row == null)
        {
            db.NdSystemSettings.Add(new NdSystemSetting
            {
                Key = GapCheckSkipLowSettingKey,
                ValueJson = json,
                UpdatedAt = DateTimeOffset.UtcNow,
                UpdatedBy = updatedBy,
            });
        }
        else
        {
            row.ValueJson = json;
            row.UpdatedAt = DateTimeOffset.UtcNow;
            row.UpdatedBy = updatedBy;
        }
        await db.SaveChangesAsync(ct);
        cache.Remove(GapCheckSkipLowMemoKey);
        return enabled;
    }

    private const string PipelineVersionSettingKey = "regul_pipeline_version";
    private const string PipelineVersionMemoKey = "regul_pipeline_version_current";

    /// <summary>Hybrid retrieval pipeline version new retrievals use (see NdRegulPipelineVersions).</summary>
    public async Task<int> GetPipelineVersionAsync(CancellationToken ct = default)
    {
        if (cache.TryGetValue(PipelineVersionMemoKey, out int cached)) return cached;
        var row = await db.NdSystemSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == PipelineVersionSettingKey, ct);
        var version = row != null && int.TryParse(row.ValueJson, out var v)
            && Reguliq.Api.Services.NewDashboard.NdRegulPipelineVersions.IsKnown(v)
                ? v
                : Reguliq.Api.Services.NewDashboard.NdRegulPipelineVersions.Default;
        cache.Set(PipelineVersionMemoKey, version, CacheTtl);
        return version;
    }

    public async Task<int> SetPipelineVersionAsync(int version, Guid updatedBy, CancellationToken ct = default)
    {
        if (!Reguliq.Api.Services.NewDashboard.NdRegulPipelineVersions.IsKnown(version))
            throw new InvalidOperationException($"Unknown pipeline version {version}.");
        var row = await db.NdSystemSettings.FirstOrDefaultAsync(s => s.Key == PipelineVersionSettingKey, ct);
        var json = version.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (row == null)
        {
            db.NdSystemSettings.Add(new NdSystemSetting
            {
                Key = PipelineVersionSettingKey,
                ValueJson = json,
                UpdatedAt = DateTimeOffset.UtcNow,
                UpdatedBy = updatedBy,
            });
        }
        else
        {
            row.ValueJson = json;
            row.UpdatedAt = DateTimeOffset.UtcNow;
            row.UpdatedBy = updatedBy;
        }
        await db.SaveChangesAsync(ct);
        cache.Remove(PipelineVersionMemoKey);
        return version;
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
