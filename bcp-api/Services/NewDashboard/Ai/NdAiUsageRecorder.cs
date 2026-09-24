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
            var rate = await pricing.GetPricingAsync(ct);

            using var dbScope = scopeFactory.CreateScope();
            var db = dbScope.ServiceProvider.GetRequiredService<AppDbContext>();
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
}
