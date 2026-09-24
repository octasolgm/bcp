using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;

namespace Reguliq.Api.Services.NewDashboard.Ai;

/// <summary>A workspace's prepaid AI credit position.</summary>
public sealed record NdAiCreditSummary(
    Guid WorkspaceId,
    decimal Granted,
    decimal Used,
    decimal Balance,
    int LowThresholdPct,
    bool Unlimited)
{
    /// <summary>Share of granted credits already spent (0-1). 0 when nothing was ever granted.</summary>
    public decimal UsedFraction => Granted <= 0 ? 0 : Math.Min(1m, Used / Granted);

    public bool IsExhausted => !Unlimited && Balance <= 0;

    /// <summary>At or past the warning line (80% by default) but not yet out of credits.</summary>
    public bool IsLow => !Unlimited && !IsExhausted && Granted > 0
        && UsedFraction >= (100 - LowThresholdPct) / 100m;
}

/// <summary>
/// Reads and moves a workspace's AI credits. The balance is always the sum of the ledger, so a top-up,
/// an AI call and a manual correction are all one insert and the history explains any number on screen.
/// </summary>
public sealed class NdAiCreditService(AppDbContext db, IMemoryCache cache)
{
    /// <summary>Workspaces with no grant at all are unlimited, so existing installs keep working until
    /// the platform admin starts handing out credits.</summary>
    private static string BalanceCacheKey(Guid workspaceId) => $"nd:ai-credits:{workspaceId}";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);

    public static void InvalidateBalance(IMemoryCache cache, Guid workspaceId) =>
        cache.Remove(BalanceCacheKey(workspaceId));

    public async Task<NdAiCreditSummary> GetSummaryAsync(Guid workspaceId, CancellationToken ct = default)
    {
        if (cache.TryGetValue<NdAiCreditSummary>(BalanceCacheKey(workspaceId), out var hit) && hit != null)
            return hit;

        var totals = await db.NdAiCreditLedger.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.TenantId == workspaceId)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Granted = g.Where(e => e.Credits > 0).Sum(e => (decimal?)e.Credits) ?? 0m,
                Spent = g.Where(e => e.Credits < 0).Sum(e => (decimal?)e.Credits) ?? 0m,
            })
            .FirstOrDefaultAsync(ct);

        var threshold = await db.NdWorkspaces.AsNoTracking()
            .Where(w => w.Id == workspaceId)
            .Select(w => (int?)w.AiCreditLowThresholdPct)
            .FirstOrDefaultAsync(ct) ?? 20;

        var granted = totals?.Granted ?? 0m;
        var used = -(totals?.Spent ?? 0m);
        var summary = new NdAiCreditSummary(
            workspaceId,
            granted,
            used,
            granted - used,
            threshold,
            Unlimited: granted <= 0);

        cache.Set(BalanceCacheKey(workspaceId), summary, CacheTtl);
        return summary;
    }

    /// <summary>Adds credits (or removes them, as an adjustment) and returns the new position.</summary>
    public async Task<NdAiCreditSummary> AddCreditsAsync(
        Guid workspaceId,
        decimal credits,
        string kind,
        Guid? actorId,
        string? note,
        CancellationToken ct = default,
        decimal usdPerCredit = 0m)
    {
        db.NdAiCreditLedger.Add(new NdAiCreditLedgerEntry
        {
            TenantId = workspaceId,
            Kind = AiCreditKinds.IsValid(kind) ? kind : AiCreditKinds.TopUp,
            Credits = credits,
            BilledUsd = usdPerCredit > 0 ? Math.Round(credits * usdPerCredit, 6, MidpointRounding.AwayFromZero) : null,
            UsdCost = 0,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            CreatedBy = actorId,
        });
        await db.SaveChangesAsync(ct);
        InvalidateBalance(cache, workspaceId);
        return await GetSummaryAsync(workspaceId, ct);
    }

    public async Task<List<NdAiCreditLedgerEntry>> RecentAsync(Guid workspaceId, int take, CancellationToken ct = default) =>
        await db.NdAiCreditLedger.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.TenantId == workspaceId)
            .OrderByDescending(e => e.CreatedAt)
            .Take(Math.Clamp(take, 1, 200))
            .ToListAsync(ct);

    /// <summary>Spend grouped by model, for the platform admin's usage view.</summary>
    public async Task<List<(string Model, decimal Credits, decimal Usd, long Calls)>> ByModelAsync(
        Guid workspaceId,
        CancellationToken ct = default)
    {
        var rows = await db.NdAiCreditLedger.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.TenantId == workspaceId && e.Kind == AiCreditKinds.Usage)
            .GroupBy(e => e.Model ?? "unknown")
            .Select(g => new
            {
                Model = g.Key,
                Credits = -g.Sum(e => e.Credits),
                Usd = g.Sum(e => e.UsdCost),
                Calls = g.LongCount(),
            })
            .OrderByDescending(x => x.Credits)
            .ToListAsync(ct);

        return rows.Select(r => (r.Model, r.Credits, r.Usd, r.Calls)).ToList();
    }
}
