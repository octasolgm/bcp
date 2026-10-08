using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Services.NewDashboard;
using Xunit;

namespace Reguliq.Api.Tests;

public class NdRegulClauseContextAndEvalTests
{
    private static Dictionary<string, NdRegulClauseContextService.Heading> Outline(params (string No, string Title)[] rows) =>
        rows.ToDictionary(r => r.No, r => new NdRegulClauseContextService.Heading(r.No, r.Title));

    private static readonly Dictionary<string, NdRegulClauseContextService.Heading> Cbuae = Outline(
        ("2", "Legal Framework"),
        ("3", "Highlights of Key Provisions"),
        ("3.1", "Summary of Minimum Statutory Obligations"),
        ("3.5", "Money Laundering"),
        ("3.6", "Predicate Offences"),
        ("3.10", "ML/FT Typologies"),
        ("3.11", "Sanctions"),
        ("4", "Risk"),
        ("4.1", "Business-wide risk"),
        ("4.1.1", "Assessing Business-wide Risks"),
        ("4.1.2", "Risk Factors"),
        ("4.2", "Methodology"));

    [Fact]
    public void Clause_context_lists_parent_siblings_in_numeric_order_and_marks_the_clause()
    {
        var ctx = NdRegulClauseContextService.Build("CBUAE", Cbuae, "3.5");

        Assert.Equal(["3"], ctx.Ancestors.Select(a => a.Number));
        Assert.Equal(["3.1", "3.5", "3.6", "3.10", "3.11"], ctx.Siblings.Select(s => s.Number));
        Assert.Empty(ctx.Children);
        Assert.Contains("3 Highlights of Key Provisions", ctx.Text);
        Assert.Contains("3.5 Money Laundering   <-- THIS CLAUSE", ctx.Text);
        Assert.Contains("3.6 Predicate Offences", ctx.Text);
        Assert.DoesNotContain("4.1", ctx.Text);
    }

    [Fact]
    public void Clause_context_includes_sub_clauses_and_top_level_siblings()
    {
        var ctx = NdRegulClauseContextService.Build("CBUAE", Cbuae, "4.1");
        Assert.Equal(["4.1.1", "4.1.2"], ctx.Children.Select(c => c.Number));
        Assert.Equal(["4.1", "4.2"], ctx.Siblings.Select(s => s.Number));

        var top = NdRegulClauseContextService.Build("CBUAE", Cbuae, "3");
        Assert.Empty(top.Ancestors);
        Assert.Equal(["2", "3", "4"], top.Siblings.Select(s => s.Number));
        Assert.Equal(5, top.Children.Count);
    }

    [Theory]
    [InlineData("3.", "3")]
    [InlineData(" 3.5 ", "3.5")]
    [InlineData("Article 4", null)]
    public void Clause_numbers_are_normalized(string raw, string? expected) =>
        Assert.Equal(expected, NdRegulClauseContextService.NormalizeNumber(raw));

    [Fact]
    public void V9_query_template_sends_the_clause_context_ahead_of_the_clause()
    {
        var template = NdRegulPromptDefaults.JudgmentUserQueryTemplateV9;
        Assert.Contains("{clause_context}", template);
        Assert.True(template.IndexOf("{clause_context}", StringComparison.Ordinal)
            < template.IndexOf("REGULATORY CLAUSE {clause_no}", StringComparison.Ordinal));
        Assert.Contains("Supporting regulatory context", NdRegulPromptDefaults.JudgmentSystemPromptV9);
    }

    private static NdAnalysisPromptVersion Version(int n, string text, DateTimeOffset created) => new()
    {
        PromptKey = "regul_judgment_user_query",
        VersionNumber = n,
        Label = $"v{n}",
        PromptText = text,
        CreatedAt = created,
    };

    [Fact]
    public void Prompt_version_is_identified_from_the_filled_text()
    {
        var t0 = DateTimeOffset.UtcNow.AddDays(-10);
        var v8 = Version(8, NdRegulPromptDefaults.JudgmentUserQueryTemplateV5, t0);
        var v9 = Version(9, NdRegulPromptDefaults.JudgmentUserQueryTemplateV9, t0.AddDays(5));

        var filledV8 = NdRegulPromptDefaults.BuildJudgmentQueryTextV5("3.5", "Money laundering is ...");
        var filledV9 = NdRegulPromptDefaults.BuildJudgmentQueryTextV9("3.5", "Money laundering is ...", "Regulation: X");

        Assert.Equal(8, NdAnalysisEvalService.IdentifyVersion([v8, v9], filledV8, t0.AddDays(1))?.VersionNumber);
        Assert.Equal(9, NdAnalysisEvalService.IdentifyVersion([v8, v9], filledV9, t0.AddDays(6))?.VersionNumber);
    }

    [Fact]
    public void Identical_versions_resolve_to_the_newest_one_created_before_the_call()
    {
        var t0 = DateTimeOffset.UtcNow.AddDays(-10);
        var text = NdRegulPromptDefaults.JudgmentUserQueryTemplateV5;
        var v7 = Version(7, text, t0);
        var v8 = Version(8, text, t0.AddDays(2));
        var filled = NdRegulPromptDefaults.BuildJudgmentQueryTextV5("1", "x");

        Assert.Equal(7, NdAnalysisEvalService.IdentifyVersion([v7, v8], filled, t0.AddDays(1))?.VersionNumber);
        Assert.Equal(8, NdAnalysisEvalService.IdentifyVersion([v7, v8], filled, t0.AddDays(3))?.VersionNumber);
    }

    [Fact]
    public void Regulation_document_is_read_from_snapshot_point_id()
    {
        var docId = Guid.Parse("7bbad517-f1cf-432f-bcb6-5f0e4f7a96e3");
        Assert.Equal(docId, NdRegulClauseContextService.RegulationDocumentIdFromSnapshot(
            "{\"pointId\": \"7bbad517-f1cf-432f-bcb6-5f0e4f7a96e3:3.5\", \"pointNumber\": \"3.5\"}"));
        Assert.Null(NdRegulClauseContextService.RegulationDocumentIdFromSnapshot("{\"pointId\": \"3.5\"}"));
        Assert.Null(NdRegulClauseContextService.RegulationDocumentIdFromSnapshot("not json"));
    }

    [Fact]
    public void Mixed_version_sets_are_detected()
    {
        Assert.True(NdAnalysisEvalService.IsSingleVersionSet("system v8, user 1 v8, user 2 v8"));
        Assert.False(NdAnalysisEvalService.IsSingleVersionSet("system v8, user 1 v9, user 2 v8"));
    }

    [Fact]
    public void Identical_texts_all_match_so_the_caller_can_disambiguate()
    {
        var t0 = DateTimeOffset.UtcNow.AddDays(-10);
        var text = NdRegulPromptDefaults.JudgmentUserContextTemplateV5;
        var v8 = Version(8, text, t0);
        var v9 = Version(9, text, t0.AddDays(5));
        var filled = NdRegulPromptDefaults.BuildJudgmentContextTextV5("some policy text");
        Assert.Equal([8, 9], NdAnalysisEvalService.MatchingVersions([v8, v9], filled).Select(v => v.VersionNumber).OrderBy(n => n));
    }

    [Fact]
    public void Numbered_items_split_inline_and_multiline_lists()
    {
        Assert.Equal(["[1] a", "[2] b"], NdAnalysisEvalService.NumberedItems("[1] a [2] b"));
        Assert.Equal(["[1] a", "[1] a2", "[2] b"], NdAnalysisEvalService.NumberedItems("[1] a\n[1] a2\n[2] b"));
        Assert.Empty(NdAnalysisEvalService.NumberedItems("N/A"));
        Assert.Equal(["plain gap"], NdAnalysisEvalService.NumberedItems("plain gap"));
    }

    private static NdAnalysisEvalService.EvalClause Clause(string no, string status, params string[] gaps) => new()
    {
        ClauseNo = no,
        OverallStatus = status,
        Gaps = [.. gaps],
    };

    [Fact]
    public void Compare_scores_status_agreement_and_gap_overlap()
    {
        var eval = new[]
        {
            Clause("3.5", "partial", "[1] Definition of funds and proceeds — Missing: no definition."),
            Clause("3.6", "partial", "[1] Predicate offence definition — Missing: dual criminality."),
            Clause("3.7", "compliant"),
        };
        var run = new[]
        {
            Clause("3.5", "partial", "[1] Definitions of funds and proceeds — Missing: the policy has no definition."),
            Clause("3.6", "compliant"),
            Clause("3.8", "partial", "[1] Something new"),
        };

        var (summary, rows) = NdAnalysisEvalService.Compare(eval, run);

        Assert.Equal(2, summary.ClausesCompared);
        Assert.Equal(1, summary.StatusMatches);
        Assert.Equal(50, summary.StatusAgreementPct);
        Assert.Equal(1, summary.OnlyInEval);
        Assert.Equal(1, summary.OnlyInRun);
        Assert.Equal(1, summary.MoreCompliant);
        Assert.Equal("same", rows.Single(r => r.ClauseNo == "3.5").Change);
        Assert.True(rows.Single(r => r.ClauseNo == "3.5").GapOverlapPct > 50);
        Assert.Equal("missing_in_run", rows.Single(r => r.ClauseNo == "3.7").Change);
        Assert.Equal("new_in_run", rows.Single(r => r.ClauseNo == "3.8").Change);
    }

    [Fact]
    public void Compare_flags_a_verdict_change_as_more_or_less_compliant()
    {
        var (summary, rows) = NdAnalysisEvalService.Compare(
            [Clause("3.6", "partial", "[1] gap"), Clause("3.5", "compliant")],
            [Clause("3.6", "compliant"), Clause("3.5", "non_compliant", "[1] gap")]);

        Assert.Equal(0, summary.StatusMatches);
        Assert.Equal(1, summary.MoreCompliant);
        Assert.Equal(1, summary.LessCompliant);
        Assert.Equal("more_compliant", rows.Single(r => r.ClauseNo == "3.6").Change);
        Assert.Equal("less_compliant", rows.Single(r => r.ClauseNo == "3.5").Change);
    }
}
