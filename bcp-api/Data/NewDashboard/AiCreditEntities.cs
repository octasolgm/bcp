using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Reguliq.Api.Data.NewDashboard.Entities;

/// <summary>
/// One line of a workspace's prepaid AI credit account: a top-up the platform admin granted, an AI call
/// that spent credits, or a manual adjustment. The balance is the sum of <see cref="Credits"/>, so every
/// change is auditable and nothing is overwritten.
///
/// Credits are an internal unit (see NdAiCreditPricing): the provider's real dollar cost is kept in
/// <see cref="UsdCost"/> so platform-side reporting stays truthful whatever rate clients are charged.
/// </summary>
[Table("nd_ai_credit_ledger")]
public class NdAiCreditLedgerEntry : ITenantScoped
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>topup | usage | adjustment</summary>
    [Column("kind")]
    public string Kind { get; set; } = AiCreditKinds.Usage;

    /// <summary>Positive for a top-up, negative for spend.</summary>
    [Column("credits")]
    public decimal Credits { get; set; }

    /// <summary>What the call actually cost us, in USD. Exact for OpenRouter, estimated elsewhere.</summary>
    [Column("usd_cost")]
    public decimal UsdCost { get; set; }

    /// <summary>What these credits were worth to the client at the rate in force when the line was written
    /// (credits x USD-per-credit). Kept on the line so changing the price later never rewrites history.
    /// Null on lines written before this column existed: reports value those at the current rate.</summary>
    [Column("billed_usd")]
    public decimal? BilledUsd { get; set; }

    /// <summary>True when UsdCost came from a price table rather than the provider's own reporting.</summary>
    [Column("usd_cost_estimated")]
    public bool UsdCostEstimated { get; set; }

    [Column("provider")]
    public string? Provider { get; set; }

    [Column("model")]
    public string? Model { get; set; }

    /// <summary>Which part of the product spent this (analysis, dual_verify, prompt_generation...).</summary>
    [Column("feature")]
    public string? Feature { get; set; }

    [Column("analysis_run_id")]
    public Guid? AnalysisRunId { get; set; }

    [Column("prompt_tokens")]
    public long PromptTokens { get; set; }

    [Column("completion_tokens")]
    public long CompletionTokens { get; set; }

    [Column("cached_tokens")]
    public long CachedTokens { get; set; }

    /// <summary>OpenRouter generation id, so a line can be reconciled against their dashboard.</summary>
    [Column("generation_id")]
    public string? GenerationId { get; set; }

    [Column("note")]
    public string? Note { get; set; }

    [Column("created_by")]
    public Guid? CreatedBy { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Guid? TenantId { get; set; }
}

public static class AiCreditKinds
{
    public const string TopUp = "topup";
    public const string Usage = "usage";
    public const string Adjustment = "adjustment";

    public static bool IsValid(string? kind) =>
        kind is TopUp or Usage or Adjustment;
}
