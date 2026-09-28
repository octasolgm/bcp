using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;

namespace Reguliq.Api.Services.NewDashboard.Ai;

/// <summary>One AI call's reported usage, as read off the provider's response.</summary>
public sealed record NdAiCallUsage(
    string Provider,
    string? Model,
    long PromptTokens,
    long CompletionTokens,
    long CachedTokens,
    decimal? ReportedUsdCost,
    string? GenerationId);

/// <summary>
/// Writes one ledger line per AI call, charged to the workspace in <see cref="NdAiUsageContext"/>.
///
/// Recording must never break an analysis: a failure here is logged and swallowed. Calls made with no
/// workspace in context (platform-level tools, startup seeding) are logged and not billed to anyone.
/// </summary>
public sealed class NdAiUsageRecorder(
    IServiceScopeFactory scopeFactory,
    NdAiPricingService pricing,
    IMemoryCache cache,
    ILogger<NdAiUsageRecorder> logger)
{
    public async Task RecordAsync(NdAiCallUsage usage, CancellationToken ct = default)
    {
        var scope = NdAiUsageContext.Value;
        if (scope?.TenantId is not Guid tenantId)
        {
            logger.LogInformation(
                "AI usage not billed (no workspace in context): provider={Provider} model={Model} in={In} out={Out}",
                usage.Provider, usage.Model, usage.PromptTokens, usage.CompletionTokens);
            return;
        }

        try
        {
            var estimated = usage.ReportedUsdCost is null;
            var usd = usage.ReportedUsdCost
                ?? await pricing.EstimateUsdAsync(usage.Model, usage.PromptTokens, usage.CompletionTokens, ct);

            using var dbScope = scopeFactory.CreateScope();
            var db = dbScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var rate = await EffectivePricingAsync(db, tenantId, ct);
            db.NdAiCreditLedger.Add(new NdAiCreditLedgerEntry
            {
                TenantId = tenantId,
                Kind = AiCreditKinds.Usage,
                // Spend is negative so the balance is a plain SUM over the ledger.
                Credits = -rate.CreditsFor(usd),
                BilledUsd = rate.BilledUsdFor(usd),
                UsdCost = usd,
                UsdCostEstimated = estimated,
                Provider = usage.Provider,
                Model = usage.Model,
                Feature = scope.Feature,
                AnalysisRunId = scope.AnalysisRunId,
                PromptTokens = usage.PromptTokens,
                CompletionTokens = usage.CompletionTokens,
                CachedTokens = usage.CachedTokens,
                GenerationId = usage.GenerationId,
                CreatedBy = scope.UserId,
            });
            await db.SaveChangesAsync(ct);
            NdAiCreditService.InvalidateBalance(cache, tenantId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Recording AI usage failed (call itself was fine).");
        }
    }

    /// <summary>
    /// Bills a non-LLM cost (currently Azure Document Intelligence OCR, priced per page) into the same
    /// ledger, so it shows up in every existing usage/cost/margin report alongside LLM spend. Uses the
    /// scope's <c>Feature</c>/workspace exactly like <see cref="RecordAsync"/>; provider/model are set to
    /// distinguish it in the "by provider"/"by model" breakdowns.
    /// </summary>
    public async Task RecordNonLlmCostAsync(
        string provider,
        string? detail,
        decimal usdCost,
        CancellationToken ct = default)
    {
        var scope = NdAiUsageContext.Value;
        if (scope?.TenantId is not Guid tenantId || usdCost <= 0) return;

        try
        {
            using var dbScope = scopeFactory.CreateScope();
            var db = dbScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var rate = await EffectivePricingAsync(db, tenantId, ct);
            db.NdAiCreditLedger.Add(new NdAiCreditLedgerEntry
            {
                TenantId = tenantId,
                Kind = AiCreditKinds.Usage,
                Credits = -rate.CreditsFor(usdCost),
                BilledUsd = rate.BilledUsdFor(usdCost),
                UsdCost = usdCost,
                UsdCostEstimated = true,
                Provider = provider,
                Model = detail,
                Feature = scope.Feature,
                AnalysisRunId = scope.AnalysisRunId,
                CreatedBy = scope.UserId,
            });
            await db.SaveChangesAsync(ct);
            NdAiCreditService.InvalidateBalance(cache, tenantId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Recording non-LLM AI cost failed ({Provider}).", provider);
        }
    }

    private async Task<NdAiCreditPricing> EffectivePricingAsync(AppDbContext db, Guid tenantId, CancellationToken ct)
    {
        var basePricing = await pricing.GetPricingAsync(ct);
        var markupOverride = await db.NdWorkspaces.AsNoTracking()
            .Where(w => w.Id == tenantId)
            .Select(w => (decimal?)w.AiMarkupOverride)
            .FirstOrDefaultAsync(ct);
        return basePricing.WithMarkupOverride(markupOverride);
    }
}
