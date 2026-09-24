using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;

namespace Reguliq.Api.Services.NewDashboard.Ai;

/// <summary>
/// Turns a provider's dollar cost into the internal credit unit clients are charged in.
///
/// credits = usd_cost * Markup / UsdPerCredit. With the defaults (1 credit = $0.01, markup 1.0) a call
/// that costs us 9 cents spends 9 credits. Both numbers are platform settings so the rate can change
/// without touching code, and past ledger lines keep the credits they were written with.
/// </summary>
public sealed record NdAiCreditPricing(decimal UsdPerCredit, decimal Markup)
{
    public const string SettingKey = "ai_credit_pricing";

    public static readonly NdAiCreditPricing Default = new(0.01m, 1.0m);

    /// <summary>The value the client is charged for a call, in USD: our cost times the markup.
    /// Always equals credits x UsdPerCredit, whatever the credit size.</summary>
    public decimal BilledUsdFor(decimal usdCost)
    {
        if (usdCost <= 0) return 0;
        var markup = Markup <= 0 ? 1m : Markup;
        return Math.Round(usdCost * markup, 6, MidpointRounding.AwayFromZero);
    }

    public decimal CreditsFor(decimal usdCost)
    {
        if (usdCost <= 0) return 0;
        var perCredit = UsdPerCredit <= 0 ? Default.UsdPerCredit : UsdPerCredit;
        var markup = Markup <= 0 ? 1m : Markup;
        return Math.Round(usdCost * markup / perCredit, 4, MidpointRounding.AwayFromZero);
    }
}

/// <summary>Reads (and caches) the credit rate and the fallback model price list from system settings.</summary>
public sealed class NdAiPricingService(IMemoryCache cache, IServiceScopeFactory scopeFactory)
{
    public const string ModelPricesSettingKey = "ai_model_prices";

    private const string PricingCacheKey = "nd:ai-credit-pricing";
    private const string PricesCacheKey = "nd:ai-model-prices";
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    /// <summary>USD per 1M tokens for providers that report tokens but not cost. Overridable per model
    /// through the <see cref="ModelPricesSettingKey"/> setting; unknown models fall back to this default,
    /// which is deliberately mid-range so an unpriced model still consumes credits.</summary>
    public sealed record ModelPrice(decimal InputPerMillion, decimal OutputPerMillion);

    private static readonly ModelPrice Fallback = new(3m, 15m);

    public void Invalidate()
    {
        cache.Remove(PricingCacheKey);
        cache.Remove(PricesCacheKey);
    }

    public async Task<NdAiCreditPricing> GetPricingAsync(CancellationToken ct = default)
    {
        if (cache.TryGetValue<NdAiCreditPricing>(PricingCacheKey, out var hit) && hit != null) return hit;

        var value = await ReadSettingAsync(NdAiCreditPricing.SettingKey, ct);
        var pricing = ParsePricing(value);

        cache.Set(PricingCacheKey, pricing, Ttl);
        return pricing;
    }

    private static readonly JsonSerializerOptions SettingJson = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Reads the stored price. The setting is written camelCase by the admin API, so the read must be
    /// case-insensitive; anything missing or invalid falls back to the default price.</summary>
    public static NdAiCreditPricing ParsePricing(string? valueJson)
    {
        if (string.IsNullOrWhiteSpace(valueJson)) return NdAiCreditPricing.Default;
        try
        {
            var parsed = JsonSerializer.Deserialize<PricingDto>(valueJson, SettingJson);
            if (parsed == null) return NdAiCreditPricing.Default;
            return new NdAiCreditPricing(
                parsed.UsdPerCredit > 0 ? parsed.UsdPerCredit : NdAiCreditPricing.Default.UsdPerCredit,
                parsed.Markup > 0 ? parsed.Markup : NdAiCreditPricing.Default.Markup);
        }
        catch (JsonException)
        {
            return NdAiCreditPricing.Default;
        }
    }

    /// <summary>Estimated USD for providers that report only tokens (OpenRouter reports real cost instead).</summary>
    public async Task<decimal> EstimateUsdAsync(string? model, long promptTokens, long completionTokens, CancellationToken ct = default)
    {
        var prices = await GetModelPricesAsync(ct);
        var price = Fallback;
        if (!string.IsNullOrWhiteSpace(model))
        {
            foreach (var (key, value) in prices)
            {
                if (model.Contains(key, StringComparison.OrdinalIgnoreCase))
                {
                    price = value;
                    break;
                }
            }
        }

        return Math.Round(
            promptTokens / 1_000_000m * price.InputPerMillion
            + completionTokens / 1_000_000m * price.OutputPerMillion,
            6,
            MidpointRounding.AwayFromZero);
    }

    private async Task<Dictionary<string, ModelPrice>> GetModelPricesAsync(CancellationToken ct)
    {
        if (cache.TryGetValue<Dictionary<string, ModelPrice>>(PricesCacheKey, out var hit) && hit != null) return hit;

        var prices = new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase);
        var value = await ReadSettingAsync(ModelPricesSettingKey, ct);
        if (value != null)
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, ModelPrice>>(value, SettingJson);
                if (parsed != null)
                {
                    foreach (var (k, v) in parsed) prices[k] = v;
                }
            }
            catch (JsonException) { /* keep empty */ }
        }

        cache.Set(PricesCacheKey, prices, Ttl);
        return prices;
    }

    private async Task<string?> ReadSettingAsync(string key, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.NdSystemSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct);
            return row?.ValueJson;
        }
        catch
        {
            // Settings table may not exist yet during bootstrap — defaults are fine.
            return null;
        }
    }

    private sealed record PricingDto(decimal UsdPerCredit, decimal Markup);
}
