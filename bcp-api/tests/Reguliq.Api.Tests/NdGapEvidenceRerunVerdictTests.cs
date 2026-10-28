using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Services.NewDashboard;
using Xunit;
using Svc = Reguliq.Api.Services.NewDashboard.NdGapEvidenceRerunService;

namespace Reguliq.Api.Tests;

public class NdGapEvidenceRerunVerdictTests
{
    private static readonly Guid PlanA = Guid.NewGuid();
    private static readonly Guid PlanB = Guid.NewGuid();
    private static readonly Guid DocId = Guid.NewGuid();

    private static Svc.ClauseWork Work() => new()
    {
        Review = new NdGapEvidenceReview(),
        Point = new NdAnalysisPoint(),
        ClauseNo = "3.3",
        ClauseText = "Banks must screen customers against sanctions lists and keep records.",
        Gaps =
        [
            new Svc.StoredGap { Index = 1, Text = "No sanctions screening procedure." },
            new Svc.StoredGap { Index = 2, Text = "No record retention period defined." },
        ],
        Actions =
        [
            new Svc.ActionRef("A1", PlanA, 1, "Adopt a sanctions screening procedure and train staff."),
            new Svc.ActionRef("A2", PlanB, 2, "Define a five-year record retention period."),
        ],
        Sections =
        [
            new Svc.ContextSection
            {
                Label = "S1",
                DocumentId = DocId,
                DocumentName = "Screening Policy v2",
                SectionRef = "4.1",
                Page = 7,
                Text = "All customers are screened against UN and local sanctions lists at onboarding and daily thereafter.",
            },
        ],
        Docs = [new Svc.EvidenceDocRef(DocId, "Screening Policy v2")],
    };

    private static Svc.EvidenceJudgment Parse(string json) =>
        NdRegulLlmJsonHelper.ParseJsonObject<Svc.EvidenceJudgment>(json);

    [Fact]
    public void Fulfilled_gap_with_verbatim_quote_resolves_its_action()
    {
        var judgment = Parse("""
        { "summary": "Screening now covered.",
          "gaps": [
            { "gap": "G1", "outcome": "fulfilled", "covered": "Screening at onboarding and daily.",
              "evidence": [ { "source": "S1", "quote": "screened against UN and local sanctions lists" } ] },
            { "gap": "G2", "outcome": "not_fulfilled", "remaining": "Retention still undefined." } ],
          "actions": [
            { "action": "A1", "outcome": "fulfilled" },
            { "action": "A2", "outcome": "not_fulfilled" } ] }
        """);

        var (gaps, actions) = Svc.EvaluateVerdicts(Work(), judgment, "Screening Policy v2");

        Assert.Equal(GapEvidenceOutcomes.Fulfilled, gaps[0].Outcome);
        Assert.Null(gaps[0].Remaining);
        var quote = Assert.Single(gaps[0].Quotes);
        Assert.True(quote.Verified);
        Assert.Equal(DocId, quote.DocumentId);
        Assert.Equal(7, quote.Page);
        Assert.Equal(GapEvidenceOutcomes.NotFulfilled, gaps[1].Outcome);
        Assert.Equal(GapEvidenceOutcomes.Fulfilled, actions[0].Outcome);
        Assert.Equal(GapEvidenceOutcomes.NotFulfilled, actions[1].Outcome);
    }

    [Fact]
    public void Fulfilled_verdict_without_any_quote_never_closes_the_gap()
    {
        var judgment = Parse("""
        { "gaps": [ { "gap": "G1", "outcome": "fulfilled", "covered": "Looks fine." } ],
          "actions": [ { "action": "A1", "outcome": "fulfilled" } ] }
        """);

        var (gaps, _) = Svc.EvaluateVerdicts(Work(), judgment, "Screening Policy v2");

        Assert.Equal(GapEvidenceOutcomes.Partial, gaps[0].Outcome);
        Assert.Contains("confirm manually", gaps[0].Remaining);
    }

    [Fact]
    public void Action_cannot_be_done_when_its_gap_is_not_addressed()
    {
        var judgment = Parse("""
        { "gaps": [ { "gap": "G2", "outcome": "not_fulfilled" } ],
          "actions": [ { "action": "A2", "outcome": "fulfilled" } ] }
        """);

        var (_, actions) = Svc.EvaluateVerdicts(Work(), judgment, "Screening Policy v2");

        Assert.Equal(GapEvidenceOutcomes.NotFulfilled, actions[1].Outcome);
        Assert.Null(actions[1].FulfilledPart);
    }

    [Fact]
    public void Partly_done_action_keeps_both_parts_for_the_split()
    {
        var judgment = Parse("""
        { "gaps": [ { "gap": 1, "outcome": "partially fulfilled", "covered": "Procedure adopted.",
                      "remaining": "Training not evidenced.",
                      "evidence": [ { "source": 1, "quote": "at onboarding and daily thereafter" } ] } ],
          "actions": [ { "action": 1, "outcome": "partially_fulfilled",
                         "fulfilled_part": "Adopt a sanctions screening procedure.",
                         "remaining_part": "Train staff on the screening procedure." } ] }
        """);

        var (gaps, actions) = Svc.EvaluateVerdicts(Work(), judgment, "Screening Policy v2");

        Assert.Equal(GapEvidenceOutcomes.Partial, gaps[0].Outcome);
        Assert.Equal("Training not evidenced.", gaps[0].Remaining);
        Assert.True(gaps[0].Quotes[0].Verified);
        Assert.Equal(GapEvidenceOutcomes.Partial, actions[0].Outcome);
        Assert.Equal("Adopt a sanctions screening procedure.", actions[0].FulfilledPart);
        Assert.Equal("Train staff on the screening procedure.", actions[0].RemainingPart);
    }

    [Fact]
    public void Partial_action_missing_its_remaining_part_is_not_split()
    {
        var judgment = Parse("""
        { "gaps": [ { "gap": "G1", "outcome": "partially_fulfilled",
                      "evidence": [ { "source": "S1", "quote": "daily thereafter" } ] } ],
          "actions": [ { "action": "A1", "outcome": "partially_fulfilled", "fulfilled_part": "Procedure adopted." } ] }
        """);

        var (_, actions) = Svc.EvaluateVerdicts(Work(), judgment, "Screening Policy v2");

        Assert.Equal(GapEvidenceOutcomes.NotFulfilled, actions[0].Outcome);
    }

    [Fact]
    public void Quote_not_in_any_section_is_kept_but_flagged()
    {
        var judgment = Parse("""
        { "gaps": [ { "gap": "G1", "outcome": "fulfilled",
                      "evidence": [ { "source": "S1", "quote": "Something the document never says." } ] } ] }
        """);

        var (gaps, _) = Svc.EvaluateVerdicts(Work(), judgment, "Screening Policy v2");

        Assert.False(Assert.Single(gaps[0].Quotes).Verified);
    }

    [Fact]
    public void List_shaped_answers_are_read_as_text()
    {
        var judgment = Parse("""
        { "summary": ["Point one.", "Point two."],
          "gaps": [ { "gap": "G1", "outcome": "partially_fulfilled",
                      "covered": ["Screening at onboarding.", "Daily screening."],
                      "remaining": ["Training not evidenced."],
                      "evidence": ["at onboarding and daily thereafter"] } ],
          "actions": [ { "action": "A1", "outcome": "partially_fulfilled",
                         "fulfilled_part": ["Adopt the procedure."], "remaining_part": ["Train staff."],
                         "reason": { "why": "Only the procedure is shown." } } ] }
        """);

        var (gaps, actions) = Svc.EvaluateVerdicts(Work(), judgment, "Screening Policy v2");

        Assert.Equal("Point one.\nPoint two.", judgment.Summary);
        Assert.Equal("Training not evidenced.", gaps[0].Remaining);
        Assert.True(Assert.Single(gaps[0].Quotes).Verified);
        Assert.Equal("Train staff.", actions[0].RemainingPart);
        Assert.Equal("Only the procedure is shown.", actions[0].Reason);
    }

    [Theory]
    [InlineData("fulfilled", GapEvidenceOutcomes.Fulfilled)]
    [InlineData("Fully covered", GapEvidenceOutcomes.Fulfilled)]
    [InlineData("partially-fulfilled", GapEvidenceOutcomes.Partial)]
    [InlineData("Partial", GapEvidenceOutcomes.Partial)]
    [InlineData("not_fulfilled", GapEvidenceOutcomes.NotFulfilled)]
    [InlineData("not covered", GapEvidenceOutcomes.NotFulfilled)]
    [InlineData("", GapEvidenceOutcomes.NotFulfilled)]
    [InlineData(null, GapEvidenceOutcomes.NotFulfilled)]
    public void Outcome_wording_is_normalized(string? raw, string expected) =>
        Assert.Equal(expected, GapEvidenceOutcomes.Normalize(raw));
}
