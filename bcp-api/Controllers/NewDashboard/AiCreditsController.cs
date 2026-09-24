using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Infrastructure.NewDashboard;
using Reguliq.Api.Services.NewDashboard.Ai;

namespace Reguliq.Api.Controllers.NewDashboard;

/// <summary>
/// AI credits: what a workspace has left, and (for the platform admin) topping it up.
///
/// A workspace's own members see only their workspace, through <c>/nd/ai-credits/me</c>. Granting credits
/// and reading another workspace's usage is platform-admin only.
/// </summary>
[ApiController]
[Route("nd/ai-credits")]
public class AiCreditsController(
    AppDbContext db,
    SupabaseJwtValidator jwt,
    NdAiCreditService credits,
    NdAiPricingService pricing,
    IMemoryCache memoryCache) : NdControllerBase
{
    public record TopUpRequest(decimal Credits, string? Note);
    public record ThresholdRequest(int LowThresholdPct);
    public record PricingRequest(decimal UsdPerCredit, decimal Markup, decimal? MinMarginPct);

    private const decimal DefaultMinMarginPct = 20m;

    private static decimal MarginPct(decimal billed, decimal cost) =>
        billed <= 0 ? 0m : Math.Round((billed - cost) / billed * 100m, 1, MidpointRounding.AwayFromZero);

    /// <summary>Warn line for the realised margin, kept beside the price in the same setting.</summary>
    private async Task<decimal> LoadMinMarginPctAsync(CancellationToken ct)
    {
        var row = await db.NdSystemSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == NdAiCreditPricing.SettingKey, ct);
        if (row == null) return DefaultMinMarginPct;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(row.ValueJson);
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (string.Equals(p.Name, "minMarginPct", StringComparison.OrdinalIgnoreCase)
                    && p.Value.TryGetDecimal(out var v) && v >= 0)
                    return v;
            }
        }
        catch (System.Text.Json.JsonException) { /* fall through */ }
        return DefaultMinMarginPct;
    }

    private static object PricingView(NdAiCreditPricing rate, decimal minMarginPct) => new
    {
        usdPerCredit = rate.UsdPerCredit,
        markup = rate.Markup,
        minMarginPct,
        creditsPerDollar = rate.UsdPerCredit > 0 ? Math.Round(1m / rate.UsdPerCredit, 2) : 0m,
        // What a client is charged and what we keep per $1.00 of provider cost.
        marginPct = rate.Markup > 0 ? Math.Round((rate.Markup - 1m) / rate.Markup * 100m, 1) : 0m,
    };

    [HttpGet("pricing")]
    public async Task<IActionResult> GetPricing(CancellationToken ct)
    {
        var (_, error) = await RequirePlatformAdminAsync(db, jwt, ct);
        if (error != null) return error;

        var rate = await pricing.GetPricingAsync(ct);
        return Ok(new { success = true, data = PricingView(rate, await LoadMinMarginPctAsync(ct)) });
    }

    /// <summary>
    /// Sets the credit price: how many dollars one credit is worth to a client (usdPerCredit) and the markup on
    /// our provider cost. Applies to AI calls from now on; ledger lines already written keep their own values.
    /// </summary>
    [HttpPut("pricing")]
    public async Task<IActionResult> SetPricing([FromBody] PricingRequest body, CancellationToken ct)
    {
        var (admin, error) = await RequirePlatformAdminAsync(db, jwt, ct);
        if (error != null) return error;

        if (body.UsdPerCredit is < 0.0001m or > 100m)
            return BadRequest(new { success = false, message = "One credit must be worth between $0.0001 and $100." });
        if (body.Markup is < 0.5m or > 20m)
            return BadRequest(new { success = false, message = "Markup must be between 0.5 and 20 (1.0 = no margin, 1.5 = we keep a third)." });
        var minMargin = body.MinMarginPct ?? DefaultMinMarginPct;
        if (minMargin is < 0 or > 95)
            return BadRequest(new { success = false, message = "The margin warning level must be between 0 and 95 percent." });

        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            usdPerCredit = body.UsdPerCredit,
            markup = body.Markup,
            minMarginPct = minMargin,
        });
        var row = await db.NdSystemSettings.FirstOrDefaultAsync(s => s.Key == NdAiCreditPricing.SettingKey, ct);
        if (row == null)
        {
            row = new NdSystemSetting { Key = NdAiCreditPricing.SettingKey };
            db.NdSystemSettings.Add(row);
        }

        row.ValueJson = json;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        row.UpdatedBy = admin.Id;
        await db.SaveChangesAsync(ct);
        pricing.Invalidate();

        var rate = await pricing.GetPricingAsync(ct);
        return Ok(new { success = true, message = "Credit price saved. It applies to new AI calls.", data = PricingView(rate, minMargin) });
    }

    /// <summary>The caller's own workspace position — every role may see it.</summary>
    [HttpGet("me")]
    public async Task<IActionResult> Mine(CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct);
        if (error != null) return error;

        var workspaceId = WorkspaceScope.CurrentWorkspaceId ?? profile.TenantId ?? WorkspaceScope.DefaultWorkspaceId;
        var summary = await credits.GetSummaryAsync(workspaceId, ct);
        var rate = await pricing.GetPricingAsync(ct);

        // Workspace users see credits, never our provider cost.
        return Ok(new
        {
            success = true,
            data = new
            {
                workspaceId = summary.WorkspaceId,
                granted = summary.Granted,
                used = summary.Used,
                balance = summary.Balance,
                unlimited = summary.Unlimited,
                isLow = summary.IsLow,
                isExhausted = summary.IsExhausted,
                usedPct = Math.Round(summary.UsedFraction * 100, 1),
                lowThresholdPct = summary.LowThresholdPct,
                usdPerCredit = rate.UsdPerCredit,
                creditsPerDollar = rate.UsdPerCredit > 0 ? Math.Round(1m / rate.UsdPerCredit, 2) : 0m,
                grantedUsd = Math.Round(summary.Granted * rate.UsdPerCredit, 4),
                usedUsd = Math.Round(summary.Used * rate.UsdPerCredit, 4),
                balanceUsd = Math.Round(summary.Balance * rate.UsdPerCredit, 4),
            },
        });
    }

    /// <summary>Recent lines for the caller's workspace, so a client can see what spent their credits.</summary>
    [HttpGet("me/history")]
    public async Task<IActionResult> MyHistory([FromQuery] int take = 50, CancellationToken ct = default)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct);
        if (error != null) return error;

        var workspaceId = WorkspaceScope.CurrentWorkspaceId ?? profile.TenantId ?? WorkspaceScope.DefaultWorkspaceId;
        var rows = await credits.RecentAsync(workspaceId, take, ct);
        var names = await LoadProfileNamesAsync(db, rows.Select(r => r.CreatedBy), ct);
        var myRate = await pricing.GetPricingAsync(ct);
        return Ok(new { success = true, data = rows.Select(r => MapEntry(r, names, includeUsd: false, myRate.UsdPerCredit)) });
    }

    [HttpGet("workspaces/{workspaceId:guid}")]
    public async Task<IActionResult> Workspace(Guid workspaceId, CancellationToken ct)
    {
        var (_, error) = await RequirePlatformAdminAsync(db, jwt, ct);
        if (error != null) return error;

        var summary = await credits.GetSummaryAsync(workspaceId, ct);
        var rows = await credits.RecentAsync(workspaceId, 50, ct);
        var names = await LoadProfileNamesAsync(db, rows.Select(r => r.CreatedBy), ct);
        var rate = await pricing.GetPricingAsync(ct);
        var perCredit = rate.UsdPerCredit > 0 ? rate.UsdPerCredit : NdAiCreditPricing.Default.UsdPerCredit;
        var minMarginPct = await LoadMinMarginPctAsync(ct);

        // Spend for this workspace with our cost and what the client was charged, side by side.
        var byModel = await db.NdAiCreditLedger.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.TenantId == workspaceId && e.Kind == AiCreditKinds.Usage)
            .GroupBy(e => e.Model ?? "unknown")
            .Select(g => new
            {
                Model = g.Key,
                Credits = -g.Sum(e => e.Credits),
                Usd = g.Sum(e => e.UsdCost),
                Billed = g.Sum(e => (decimal?)(e.BilledUsd ?? (-e.Credits * perCredit))) ?? 0m,
                Calls = g.LongCount(),
            })
            .OrderByDescending(x => x.Credits)
            .ToListAsync(ct);
        var totalCost = byModel.Sum(m => m.Usd);
        var totalBilled = byModel.Sum(m => m.Billed);

        return Ok(new
        {
            success = true,
            data = new
            {
                summary = new
                {
                    workspaceId = summary.WorkspaceId,
                    granted = summary.Granted,
                    used = summary.Used,
                    balance = summary.Balance,
                    unlimited = summary.Unlimited,
                    isLow = summary.IsLow,
                    isExhausted = summary.IsExhausted,
                    usedPct = Math.Round(summary.UsedFraction * 100, 1),
                    lowThresholdPct = summary.LowThresholdPct,
                    usdPerCredit = rate.UsdPerCredit,
                    creditsPerDollar = Math.Round(1m / perCredit, 2),
                    grantedUsd = Math.Round(summary.Granted * perCredit, 4),
                    balanceUsd = Math.Round(summary.Balance * perCredit, 4),
                    // Platform admin only: what the AI cost us against what this client was charged.
                    costUsd = totalCost,
                    billedUsd = totalBilled,
                    marginUsd = totalBilled - totalCost,
                    marginPct = MarginPct(totalBilled, totalCost),
                    minMarginPct,
                },
                byModel = byModel.Select(m => new
                {
                    model = m.Model,
                    credits = m.Credits,
                    usd = m.Usd,
                    billedUsd = m.Billed,
                    marginUsd = m.Billed - m.Usd,
                    calls = m.Calls,
                }),
                history = rows.Select(r => MapEntry(r, names, includeUsd: true, perCredit)),
            },
        });
    }

    /// <summary>
    /// Usage across every workspace, filtered. This is the platform admin's reporting view: which client
    /// spent what, on which model, over which period, with our real dollar cost alongside the credits.
    /// </summary>
    [HttpGet("usage")]
    public async Task<IActionResult> Usage(
        [FromQuery] Guid? workspaceId,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] string? model,
        [FromQuery] string? feature,
        [FromQuery] string? kind,
        [FromQuery] int take = 100,
        CancellationToken ct = default)
    {
        var (_, error) = await RequirePlatformAdminAsync(db, jwt, ct);
        if (error != null) return error;

        // Ledger rows are workspace-owned; this view is deliberately cross-workspace, so the tenant
        // filter is bypassed and the workspace becomes just another filter the admin can set.
        var q = db.NdAiCreditLedger.IgnoreQueryFilters().AsNoTracking();
        if (workspaceId is Guid ws) q = q.Where(e => e.TenantId == ws);
        if (from is DateTimeOffset f) q = q.Where(e => e.CreatedAt >= f);
        if (to is DateTimeOffset t) q = q.Where(e => e.CreatedAt <= t);
        if (!string.IsNullOrWhiteSpace(model)) q = q.Where(e => e.Model != null && e.Model.Contains(model));
        if (!string.IsNullOrWhiteSpace(feature)) q = q.Where(e => e.Feature == feature);
        if (AiCreditKinds.IsValid(kind)) q = q.Where(e => e.Kind == kind);

        var spend = q.Where(e => e.Kind == AiCreditKinds.Usage);
        var rate = await pricing.GetPricingAsync(ct);
        var perCredit = rate.UsdPerCredit > 0 ? rate.UsdPerCredit : NdAiCreditPricing.Default.UsdPerCredit;
        var minMarginPct = await LoadMinMarginPctAsync(ct);

        var totals = await spend
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Credits = -(g.Sum(e => (decimal?)e.Credits) ?? 0m),
                Usd = g.Sum(e => (decimal?)e.UsdCost) ?? 0m,
                Billed = g.Sum(e => (decimal?)(e.BilledUsd ?? (-e.Credits * perCredit))) ?? 0m,
                Calls = g.LongCount(),
                PromptTokens = g.Sum(e => (long?)e.PromptTokens) ?? 0,
                CompletionTokens = g.Sum(e => (long?)e.CompletionTokens) ?? 0,
            })
            .FirstOrDefaultAsync(ct);

        var perWorkspace = await spend
            .GroupBy(e => e.TenantId)
            .Select(g => new
            {
                WorkspaceId = g.Key,
                Credits = -g.Sum(e => e.Credits),
                Usd = g.Sum(e => e.UsdCost),
                Billed = g.Sum(e => (decimal?)(e.BilledUsd ?? (-e.Credits * perCredit))) ?? 0m,
                Calls = g.LongCount(),
            })
            .ToListAsync(ct);

        var perModel = await spend
            .GroupBy(e => e.Model ?? "unknown")
            .Select(g => new
            {
                Model = g.Key,
                Credits = -g.Sum(e => e.Credits),
                Usd = g.Sum(e => e.UsdCost),
                Billed = g.Sum(e => (decimal?)(e.BilledUsd ?? (-e.Credits * perCredit))) ?? 0m,
                Calls = g.LongCount(),
            })
            .OrderByDescending(x => x.Credits)
            .ToListAsync(ct);

        var rows = await q
            .OrderByDescending(e => e.CreatedAt)
            .Take(Math.Clamp(take, 1, 500))
            .ToListAsync(ct);

        var names = await LoadProfileNamesAsync(db, rows.Select(r => r.CreatedBy), ct);
        var workspaceNames = await db.NdWorkspaces.AsNoTracking()
            .ToDictionaryAsync(w => w.Id, w => w.Name, ct);

        // Balances come from the ledger as a whole, never from the filtered window.
        var balances = new Dictionary<Guid, decimal>();
        foreach (var id in perWorkspace.Select(p => p.WorkspaceId).Where(id => id.HasValue).Select(id => id!.Value))
            balances[id] = (await credits.GetSummaryAsync(id, ct)).Balance;

        return Ok(new
        {
            success = true,
            data = new
            {
                totals = new
                {
                    credits = totals?.Credits ?? 0m,
                    usd = totals?.Usd ?? 0m,
                    billedUsd = totals?.Billed ?? 0m,
                    marginUsd = (totals?.Billed ?? 0m) - (totals?.Usd ?? 0m),
                    marginPct = MarginPct(totals?.Billed ?? 0m, totals?.Usd ?? 0m),
                    minMarginPct,
                    calls = totals?.Calls ?? 0,
                    promptTokens = totals?.PromptTokens ?? 0,
                    completionTokens = totals?.CompletionTokens ?? 0,
                },
                byWorkspace = perWorkspace
                    .OrderByDescending(p => p.Credits)
                    .Select(p => new
                    {
                        workspaceId = p.WorkspaceId,
                        name = p.WorkspaceId is Guid id && workspaceNames.TryGetValue(id, out var n) ? n : "(no workspace)",
                        credits = p.Credits,
                        usd = p.Usd,
                        billedUsd = p.Billed,
                        marginUsd = p.Billed - p.Usd,
                        marginPct = MarginPct(p.Billed, p.Usd),
                        calls = p.Calls,
                        balance = p.WorkspaceId is Guid bid && balances.TryGetValue(bid, out var b) ? b : (decimal?)null,
                    }),
                byModel = perModel.Select(m => new
                {
                    model = m.Model,
                    credits = m.Credits,
                    usd = m.Usd,
                    billedUsd = m.Billed,
                    marginUsd = m.Billed - m.Usd,
                    marginPct = MarginPct(m.Billed, m.Usd),
                    calls = m.Calls,
                }),
                pricing = PricingView(rate, minMarginPct),
                rows = rows.Select(r => new
                {
                    id = r.Id,
                    workspaceId = r.TenantId,
                    workspaceName = r.TenantId is Guid wid && workspaceNames.TryGetValue(wid, out var wn) ? wn : null,
                    kind = r.Kind,
                    credits = r.Credits,
                    usd = r.UsdCost,
                    billedUsd = LineBilledUsd(r, perCredit),
                    usdEstimated = r.UsdCostEstimated,
                    provider = r.Provider,
                    model = r.Model,
                    feature = r.Feature,
                    analysisRunId = r.AnalysisRunId,
                    promptTokens = r.PromptTokens,
                    completionTokens = r.CompletionTokens,
                    note = r.Note,
                    by = ProfileName(names, r.CreatedBy),
                    createdAt = r.CreatedAt,
                }),
                // Filter choices come from the data itself, so the pickers only offer what exists.
                filters = new
                {
                    workspaces = workspaceNames.Select(w => new { id = w.Key, name = w.Value }).OrderBy(w => w.name),
                    models = await db.NdAiCreditLedger.IgnoreQueryFilters().AsNoTracking()
                        .Where(e => e.Model != null)
                        .Select(e => e.Model!)
                        .Distinct()
                        .OrderBy(m => m)
                        .ToListAsync(ct),
                    features = await db.NdAiCreditLedger.IgnoreQueryFilters().AsNoTracking()
                        .Where(e => e.Feature != null)
                        .Select(e => e.Feature!)
                        .Distinct()
                        .OrderBy(f => f)
                        .ToListAsync(ct),
                },
            },
        });
    }

    [HttpPost("workspaces/{workspaceId:guid}/top-up")]
    public async Task<IActionResult> TopUp(Guid workspaceId, [FromBody] TopUpRequest body, CancellationToken ct)
    {
        var (admin, error) = await RequirePlatformAdminAsync(db, jwt, ct);
        if (error != null) return error;

        if (body.Credits == 0)
            return BadRequest(new { success = false, message = "Enter how many credits to add." });
        if (Math.Abs(body.Credits) > 10_000_000m)
            return BadRequest(new { success = false, message = "That is more credits than this can grant at once." });
        if (!await db.NdWorkspaces.AnyAsync(w => w.Id == workspaceId, ct))
            return NotFound(new { success = false, message = "Workspace not found." });

        // A negative amount is a correction, not a grant, and is labelled as such in the history.
        var kind = body.Credits > 0 ? AiCreditKinds.TopUp : AiCreditKinds.Adjustment;
        var topUpRate = await pricing.GetPricingAsync(ct);
        var summary = await credits.AddCreditsAsync(
            workspaceId, body.Credits, kind, admin.Id, body.Note, ct, topUpRate.UsdPerCredit);

        return Ok(new
        {
            success = true,
            message = body.Credits > 0
                ? $"Added {body.Credits:0.##} credits."
                : $"Removed {Math.Abs(body.Credits):0.##} credits.",
            data = new { granted = summary.Granted, used = summary.Used, balance = summary.Balance },
        });
    }

    [HttpPut("workspaces/{workspaceId:guid}/threshold")]
    public async Task<IActionResult> Threshold(Guid workspaceId, [FromBody] ThresholdRequest body, CancellationToken ct)
    {
        var (_, error) = await RequirePlatformAdminAsync(db, jwt, ct);
        if (error != null) return error;

        if (body.LowThresholdPct is < 1 or > 90)
            return BadRequest(new { success = false, message = "Warning level must be between 1 and 90 percent." });

        var ws = await db.NdWorkspaces.FirstOrDefaultAsync(w => w.Id == workspaceId, ct);
        if (ws == null) return NotFound(new { success = false, message = "Workspace not found." });

        ws.AiCreditLowThresholdPct = body.LowThresholdPct;
        ws.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        NdAiCreditService.InvalidateBalance(memoryCache, workspaceId);

        return Ok(new { success = true, data = new { lowThresholdPct = ws.AiCreditLowThresholdPct } });
    }

    /// <summary>What a ledger line was worth to the client: the value stored at write time, else credits at the
    /// current rate (lines written before the value was stored).</summary>
    private static decimal LineBilledUsd(NdAiCreditLedgerEntry e, decimal perCredit) =>
        e.BilledUsd ?? Math.Round(
            (e.Kind == AiCreditKinds.Usage ? Math.Abs(e.Credits) : e.Credits) * perCredit,
            6,
            MidpointRounding.AwayFromZero);

    private static object MapEntry(
        NdAiCreditLedgerEntry e,
        IReadOnlyDictionary<Guid, string> names,
        bool includeUsd,
        decimal perCredit) => new
    {
        id = e.Id,
        kind = e.Kind,
        credits = e.Credits,
        billedUsd = LineBilledUsd(e, perCredit),
        model = e.Model,
        provider = e.Provider,
        feature = e.Feature,
        analysisRunId = e.AnalysisRunId,
        promptTokens = e.PromptTokens,
        completionTokens = e.CompletionTokens,
        note = e.Note,
        by = ProfileName(names, e.CreatedBy),
        createdAt = e.CreatedAt,
        usd = includeUsd ? e.UsdCost : (decimal?)null,
        usdEstimated = includeUsd ? e.UsdCostEstimated : (bool?)null,
    };
}
