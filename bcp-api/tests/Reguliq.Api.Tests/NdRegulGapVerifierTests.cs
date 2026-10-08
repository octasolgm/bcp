using Reguliq.Api.Services.NewDashboard;
using Xunit;

namespace Reguliq.Api.Tests;

public class NdRegulGapVerifierTests
{
    private const string Gaps =
        "[1] Adopt the definitions of funds and proceeds (clause: \"define \"funds\" in a very broad sense\") - Missing: no definition of funds or proceeds - Materiality: low\n" +
        "[2] Timeframe and nature of funds irrelevant to suspicion (clause: \"the timeframe during which it took place\") - Missing: only the amount is addressed - Materiality: low\n" +
        "[3] Assets of any form (clause: \"any number of tangible or intangible assets\") - Missing: no statement on non-cash assets - Materiality: low";

    private const string Actions =
        "[1] Amend the Definitions section to include: \"Funds means ...\"\n" +
        "[2] Amend Section 7.7 to include: \"The timeframe ... is irrelevant.\"\n" +
        "[3] Amend the Definitions section to include: \"Money laundering may involve any asset.\"";

    private static RegulJudgmentResult Judgment() => new()
    {
        OverallStatus = "partial",
        DesignStatus = "partial",
        OperatingStatus = "partial",
        Confidence = 0.7,
        Interpretation = "definition clause",
        CoveredElements = "[1] ML definition - Covered: [AML Manual — Introduction p.6]",
        DocumentReference = "AML Manual — Introduction p.6",
        PolicyExtract = ["Any person who knows that funds are proceeded by an original offence"],
        GapDescription = Gaps,
        SuggestedAction = Actions,
        GapDirection = "missing_in_internal",
    };

    private static readonly NdRegulGapVerifier.Excerpt Duration = new(
        "E2",
        "Implementation Manual — 17.2 p.22",
        "If the activity takes place over a period of time, provide the date when the suspicious activity or transaction was first observed and describe the duration of the activity.");

    private static readonly NdRegulGapVerifier.Excerpt Crypto = new(
        "E5",
        "AML Manual — Annex 1 p.62",
        "How it works: Utilizing emerging or new payment technologies such as virtual currencies/cryptocurrencies, peer-to-peer (P2P) lending etc. to facilitate money laundering");

    [Fact]
    public void Gap_lines_give_the_requirement_and_the_clause_words()
    {
        var gaps = NdRegulGapVerifier.ParseGaps(Gaps);

        Assert.Equal(3, gaps.Count);
        Assert.Equal("Timeframe and nature of funds irrelevant to suspicion", gaps[1].Requirement);
        Assert.Equal("the timeframe during which it took place", gaps[1].ClauseWords);
        Assert.Equal(
            ["Timeframe and nature of funds irrelevant to suspicion", "the timeframe during which it took place"],
            NdRegulGapVerifier.QueriesFor(gaps[1]));
        Assert.Empty(NdRegulGapVerifier.ParseGaps("N/A"));
    }

    [Fact]
    public void A_covered_answer_counts_only_when_its_quote_is_verbatim_in_the_named_excerpt()
    {
        var gap = NdRegulGapVerifier.ParseGaps(Gaps)[1];
        var excerpts = new[] { Duration, Crypto };

        var good = NdRegulGapVerifier.Decide(gap, new NdRegulGapVerifier.Answer
        {
            Status = "covered", Evidence = "E2", Quote = "describe the duration of the activity",
        }, excerpts);
        Assert.Equal("covered", good.Status);
        Assert.Same(Duration, good.Evidence);

        var wrongExcerpt = NdRegulGapVerifier.Decide(gap, new NdRegulGapVerifier.Answer
        {
            Status = "covered", Evidence = "E5", Quote = "describe the duration of the activity",
        }, excerpts);
        Assert.Equal("not_covered", wrongExcerpt.Status);

        var invented = NdRegulGapVerifier.Decide(gap, new NdRegulGapVerifier.Answer
        {
            Status = "covered", Evidence = "E2", Quote = "the timeframe of a transaction is irrelevant to reporting",
        }, excerpts);
        Assert.Equal("not_covered", invented.Status);
    }

    [Fact]
    public void Answer_parsing_tolerates_fences_and_unknown_status()
    {
        var a = NdRegulGapVerifier.ParseAnswer("```json\n{\"status\":\"Covered\",\"evidence\":\"[E2]\",\"quote\":\"x y z\",\"reason\":\"r\"}\n```");
        Assert.Equal("covered", a.Status);
        Assert.Equal("not_covered", NdRegulGapVerifier.ParseAnswer("{\"status\":\"maybe\"}").Status);
        Assert.Equal("not_covered", NdRegulGapVerifier.ParseAnswer("not json at all").Status);
    }

    [Fact]
    public void Covered_gaps_become_covered_elements_and_the_rest_are_renumbered()
    {
        var gaps = NdRegulGapVerifier.ParseGaps(Gaps);
        var outcomes = new[]
        {
            new NdRegulGapVerifier.Outcome(gaps[0], "not_covered", null, "", ""),
            new NdRegulGapVerifier.Outcome(gaps[1], "covered", Duration, "describe the duration of the activity", ""),
            new NdRegulGapVerifier.Outcome(gaps[2], "covered", Crypto, "virtual currencies/cryptocurrencies", ""),
        };

        var result = NdRegulGapVerifier.Apply(Judgment(), outcomes);

        Assert.Equal("partial", result.OverallStatus);
        var remaining = NdRegulGapVerifier.NumberedLines(result.GapDescription);
        Assert.Single(remaining);
        Assert.Equal(1, remaining[0].Number);
        Assert.StartsWith("Adopt the definitions of funds and proceeds", remaining[0].Text);
        var actions = NdRegulGapVerifier.NumberedLines(result.SuggestedAction);
        Assert.Single(actions);
        Assert.Contains("Funds means", actions[0].Text);

        var covered = NdRegulGapVerifier.NumberedLines(result.CoveredElements);
        Assert.Equal(3, covered.Count);
        Assert.Contains(covered, c => c.Text.Contains("[Implementation Manual — 17.2 p.22]", StringComparison.Ordinal));
        Assert.Contains("describe the duration of the activity", result.PolicyExtract);
        Assert.Contains("AML Manual — Annex 1 p.62", result.DocumentReference);
    }

    [Fact]
    public void All_gaps_covered_makes_the_clause_compliant()
    {
        var gaps = NdRegulGapVerifier.ParseGaps(Gaps);
        var outcomes = gaps.Select(g => new NdRegulGapVerifier.Outcome(g, "covered", Duration, "describe the duration of the activity", "")).ToList();

        var result = NdRegulGapVerifier.Apply(Judgment(), outcomes);

        Assert.Equal("compliant", result.OverallStatus);
        Assert.Equal("compliant", result.DesignStatus);
        Assert.Equal("N/A", result.GapDescription);
        Assert.Equal("N/A", result.SuggestedAction);
        Assert.Equal("", result.GapDirection);
    }

    [Fact]
    public void A_partly_covered_gap_stays_with_a_note_and_nothing_changes_without_evidence()
    {
        var gaps = NdRegulGapVerifier.ParseGaps(Gaps);
        var partial = NdRegulGapVerifier.Apply(Judgment(), [new NdRegulGapVerifier.Outcome(gaps[2], "partial", Crypto, "virtual currencies/cryptocurrencies", "")]);
        Assert.Equal(3, NdRegulGapVerifier.NumberedLines(partial.GapDescription).Count);
        Assert.Contains("Partly addressed: [AML Manual — Annex 1 p.62]", partial.GapDescription);

        var unchanged = NdRegulGapVerifier.Apply(Judgment(), gaps.Select(g => new NdRegulGapVerifier.Outcome(g, "not_covered", null, "", "")).ToList());
        Assert.Equal(Gaps, unchanged.GapDescription);
        Assert.Equal("partial", unchanged.OverallStatus);
    }
}

public class NdRegulPromptV10Tests
{
    [Fact]
    public void Prompt_v10_is_valid_and_has_no_examples_from_one_banks_documents()
    {
        NdAnalysisPromptVersionService.ValidatePromptText(
            NdAnalysisPromptVersionService.JudgmentUserQueryKey, NdRegulPromptDefaults.JudgmentUserQueryTemplateV10);
        Assert.Contains("{clause_context}", NdRegulPromptDefaults.JudgmentUserQueryTemplateV10);
        foreach (var text in new[] { NdRegulPromptDefaults.JudgmentSystemPromptV10, NdRegulPromptDefaults.JudgmentUserQueryTemplateV10 })
        {
            Assert.DoesNotContain("DIFC", text);
            Assert.DoesNotContain("9.4.1", text);
            Assert.DoesNotContain("Predicate Offences", text);
        }
        Assert.Contains("Materiality", NdRegulPromptDefaults.JudgmentSystemPromptV10);
        Assert.Contains("such as", NdRegulPromptDefaults.JudgmentSystemPromptV10);
    }
}

public class NdRegulPromptV11Tests
{
    [Fact]
    public void Prompt_v11_is_valid_and_keeps_a_formally_defined_term_as_its_own_requirement()
    {
        NdAnalysisPromptVersionService.ValidatePromptText(
            NdAnalysisPromptVersionService.JudgmentUserQueryKey, NdRegulPromptDefaults.JudgmentUserQueryTemplateV11);
        Assert.Contains("{clause_context}", NdRegulPromptDefaults.JudgmentUserQueryTemplateV11);
        foreach (var text in new[] { NdRegulPromptDefaults.JudgmentSystemPromptV11, NdRegulPromptDefaults.JudgmentUserQueryTemplateV11 })
        {
            Assert.DoesNotContain("DIFC", text);
            Assert.DoesNotContain("funds", text);
            Assert.Contains("adopt", text);
        }
        Assert.Contains("TERM THE CLAUSE FORMALLY DEFINES", NdRegulPromptDefaults.JudgmentSystemPromptV11);
        Assert.Contains("never fold it into a scope requirement", NdRegulPromptDefaults.JudgmentSystemPromptV11);
        Assert.Contains("covered only when EVERY element has its own evidence", NdRegulPromptDefaults.JudgmentSystemPromptV11);
        Assert.Contains("never applies to an illustrative list", NdRegulPromptDefaults.JudgmentSystemPromptV11);
    }

    [Fact]
    public void Gap_check_does_not_accept_typologies_as_a_missing_definition()
    {
        var gap = NdRegulGapVerifier.ParseGaps("[1] Definitions of the defined terms (clause: \"define\") - Missing: no definition - Materiality: low")[0];
        var prompt = NdRegulGapVerifier.BuildPrompt("3.5", "clause text", gap, [new NdRegulGapVerifier.Excerpt("E1", "Doc p.1", "text")]);
        Assert.Contains("uses of the term, examples, typologies or red flags do not", prompt);
    }
}
