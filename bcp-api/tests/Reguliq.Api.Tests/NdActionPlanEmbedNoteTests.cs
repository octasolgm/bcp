using Reguliq.Api.Services.NewDashboard.CorrectedDocs;
using Xunit;

namespace Reguliq.Api.Tests;

public class NdActionPlanEmbedNoteTests
{
    private static NdActionPlanEmbedTarget Target(int? page = 29, string clauseNo = "3.1") => new(
        StoredDocumentId: Guid.NewGuid(),
        Page: page,
        AnchorText: "record retention",
        ClauseNo: clauseNo,
        ClauseTitle: "Summary of Minimum Statutory Obligations",
        GapText: "No explicit general record-retention policy shown in the excerpts.",
        ActionText: "Added a dedicated record-retention section citing the 10-year requirement.",
        ResponsibilityLabel: "Compliance Department",
        ResolvedAt: new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero),
        ResolvedByName: "GM Rehman");

    [Fact]
    public void Note_names_the_exact_clause_it_fulfills()
    {
        var note = NdActionPlanEmbedNote.Build(Target());
        Assert.Contains("This action plan fulfills Regulatory Clause 3.1", note);
    }

    [Fact]
    public void Note_cites_the_page_when_known()
    {
        var note = NdActionPlanEmbedNote.Build(Target(page: 29));
        Assert.Contains("referenced at p.29 of this document", note);
    }

    [Fact]
    public void Note_omits_the_page_citation_when_page_is_unknown()
    {
        var note = NdActionPlanEmbedNote.Build(Target(page: null));
        Assert.Contains("This action plan fulfills Regulatory Clause 3.1.", note);
        Assert.DoesNotContain("referenced at p.", note);
    }

    [Fact]
    public void Note_includes_the_gap_and_action_text_and_who_resolved_it()
    {
        var note = NdActionPlanEmbedNote.Build(Target());
        Assert.Contains("Gap identified: No explicit general record-retention policy", note);
        Assert.Contains("Action taken: Added a dedicated record-retention section", note);
        Assert.Contains("Compliance Department", note);
        Assert.Contains("GM Rehman", note);
        Assert.Contains("26 Sep 2026", note);
    }

    [Fact]
    public void Note_starts_with_a_clause_labeled_header()
    {
        var note = NdActionPlanEmbedNote.Build(Target());
        Assert.StartsWith("COMPLIANCE ACTION — Clause 3.1", note);
    }

    [Fact]
    public void Generated_body_uses_policy_update_header_not_raw_action_plan()
    {
        var t = Target() with
        {
            GeneratedEmbedBody =
                "The Bank defines money laundering as an independent crime from the predicate offence, "
                + "whether committed inside or outside the UAE, consistent with AML-CFT Law Articles 2.1–3.",
        };
        var note = NdActionPlanEmbedNote.Build(t);
        Assert.StartsWith("POLICY UPDATE — Regulatory Clause 3.1", note);
        Assert.Contains("independent crime from the predicate offence", note);
        Assert.DoesNotContain("Gap identified:", note);
        Assert.DoesNotContain("Action taken:", note);
        Assert.Contains("This update addresses Regulatory Clause 3.1", note);
    }
}
