using Reguliq.Api.Services.NewDashboard;
using Xunit;

namespace Reguliq.Api.Tests;

/// <summary>V5 (hybrid) only: compliant means no gap and no action; a gap always comes with an action plan.</summary>
public class NdRegulHybridConsistencyTests
{
    private static RegulJudgmentResult Judgment(string status, string gap = "", string action = "") => new()
    {
        DesignStatus = status,
        OperatingStatus = status,
        OverallStatus = status,
        Confidence = 0.85,
        Interpretation = "reasoning",
        PolicyExtract = ["quote"],
        DocumentReference = "Manual.pdf - 7.5 p.29",
        GapDescription = gap,
        SuggestedAction = action,
        GapDirection = string.IsNullOrEmpty(gap) ? "" : "policy_gap",
    };

    [Fact]
    public void Compliant_verdict_clears_gap_and_action_even_when_the_model_filled_them()
    {
        var j = NdRegulJudgmentPostProcessor.ApplyStatusConsistency(
            Judgment("compliant", "8. Maintain adequate records: partially covered", "Minor: consolidate record-keeping"));

        Assert.Equal("", j.GapDescription);
        Assert.Equal("", j.SuggestedAction);
        Assert.Equal("", j.GapDirection);
        Assert.False(NdRegulJudgmentPostProcessor.RequiresGapOrActionRetry(j));
    }

    [Fact]
    public void Compliant_message_shows_N_A_for_gap_and_corrective_action()
    {
        var j = NdRegulJudgmentPostProcessor.ApplyStatusConsistency(Judgment("compliant", "some gap", "some action"));
        var message = NdRegulJudgmentFormatter.FormatLandingMessage("3.1", "clause text", j);

        Assert.Contains("Comply Yes/No (Status) : Compliant", message);
        Assert.Contains("Gap analysis :\nN/A\nCorrective Action Plan :\nN/A", message.Replace("\r\n", "\n"));
    }

    [Theory]
    [InlineData("partial")]
    [InlineData("non_compliant")]
    public void Gap_without_action_needs_a_retry(string status)
    {
        Assert.True(NdRegulJudgmentPostProcessor.RequiresGapOrActionRetry(Judgment(status, "records missing", "")));
        Assert.True(NdRegulJudgmentPostProcessor.RequiresGapOrActionRetry(Judgment(status, "records missing", "N/A")));
        Assert.True(NdRegulJudgmentPostProcessor.RequiresGapOrActionRetry(Judgment(status, "", "add a retention section")));
    }

    [Fact]
    public void Gap_with_gap_and_action_needs_no_retry()
    {
        Assert.False(NdRegulJudgmentPostProcessor.RequiresGapOrActionRetry(
            Judgment("partial", "records missing", "add a retention section")));
    }

    [Fact]
    public void Consistency_does_not_touch_a_partial_verdict()
    {
        var j = NdRegulJudgmentPostProcessor.ApplyStatusConsistency(Judgment("partial", "gap", "action"));
        Assert.Equal("gap", j.GapDescription);
        Assert.Equal("action", j.SuggestedAction);
    }

    [Fact]
    public void Fallback_adds_an_action_plan_derived_from_the_gap()
    {
        var j = NdRegulJudgmentPostProcessor.EnsureActionPlanForGap(Judgment("partial", "No retention period stated.", ""));
        Assert.False(string.IsNullOrWhiteSpace(j.SuggestedAction));
        Assert.Contains("No retention period stated.", j.SuggestedAction);

        var none = NdRegulJudgmentPostProcessor.EnsureActionPlanForGap(Judgment("non_compliant", "", ""));
        Assert.False(string.IsNullOrWhiteSpace(none.SuggestedAction));
    }

    [Fact]
    public void Fallback_leaves_compliant_and_existing_actions_alone()
    {
        Assert.Equal("", NdRegulJudgmentPostProcessor.EnsureActionPlanForGap(Judgment("compliant")).SuggestedAction);
        Assert.Equal("keep", NdRegulJudgmentPostProcessor.EnsureActionPlanForGap(Judgment("partial", "gap", "keep")).SuggestedAction);
    }

    [Fact]
    public void Gap_message_always_has_a_corrective_action_after_fallback()
    {
        var j = NdRegulJudgmentPostProcessor.EnsureActionPlanForGap(Judgment("partial", "No retention period stated.", ""));
        var message = NdRegulJudgmentFormatter.FormatLandingMessage("3.1", "clause text", j).Replace("\r\n", "\n");
        Assert.DoesNotContain("Corrective Action Plan :\n—", message);
        Assert.DoesNotContain("Corrective Action Plan :\nN/A", message);
    }

    [Fact]
    public void Hybrid_retry_note_names_what_is_missing()
    {
        Assert.Contains("suggested_action was empty", NdRegulPromptDefaults.BuildHybridJudgmentRetryNote("partial", false, true));
        Assert.Contains("gap_description was empty", NdRegulPromptDefaults.BuildHybridJudgmentRetryNote("partial", true, false));
        Assert.Contains("gap_description and suggested_action", NdRegulPromptDefaults.BuildHybridJudgmentRetryNote("partial", true, true));
    }

    [Fact]
    public void Original_retry_note_used_by_other_engines_is_unchanged()
    {
        Assert.StartsWith("--- RETRY ---", NdRegulPromptDefaults.BuildJudgmentRetryNote("partial"));
        Assert.DoesNotContain("suggested_action", NdRegulPromptDefaults.BuildJudgmentRetryNote("partial"));
    }
}
