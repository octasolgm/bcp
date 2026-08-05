using Reguliq.Api.Services.NewDashboard;
using Xunit;

namespace Reguliq.Api.Tests;

public class NdRegulJudgmentPostProcessorTests
{
    [Fact]
    public void ApplyGroundedDocumentReference_uses_quote_location_in_markdown()
    {
        var markdown =
            "<!-- BCP_PDF_PAGE:10 -->\n" +
            "Front matter only.\n" +
            "<!-- BCP_PDF_PAGE:42 -->\n" +
            "6.2.2 Customer review\n" +
            "Occasional transactions must be reviewed and customer identity verified before processing.";

        var chunks = new List<NdRegulPolicyContextService.PolicyChunk>
        {
            new(
                "Manual.pdf — 6.2.2 p.12",
                "Occasional transactions must be reviewed and customer identity verified before processing.",
                "Manual.pdf",
                "6.2.2",
                12),
        };

        var judgment = new RegulJudgmentResult
        {
            PolicyExtract = ["Occasional transactions must be reviewed and customer identity verified before processing."],
            DocumentReference = "Manual.pdf section 7.4 page 99",
        };

        var markdownByFile = new Dictionary<string, string> { ["Manual.pdf"] = markdown };
        var result = NdRegulJudgmentPostProcessor.ApplyGroundedDocumentReference(
            judgment,
            chunks,
            markdownByFile);

        Assert.Contains("p.42", result.DocumentReference);
        Assert.Contains("section 6.2.2", result.DocumentReference);
        Assert.DoesNotContain("99", result.DocumentReference);
        Assert.DoesNotContain("p.12", result.DocumentReference);
    }

    [Fact]
    public void ApplyGroundedDocumentReference_uses_heading_near_quote_not_wrong_chunk_section()
    {
        var markdown =
            "<!-- BCP_PDF_PAGE:12 -->\n" +
            "6.2 Independence of the Compliance Department\n" +
            "The department has complete independence.\n" +
            "<!-- BCP_PDF_PAGE:15 -->\n" +
            "6.8 Other topic\n" +
            "Unrelated content.\n" +
            "<!-- BCP_PDF_PAGE:42 -->\n" +
            "9.4.1 Internal Audit AML\n" +
            "Internal audit tests the AML programme annually.";

        var chunks = new List<NdRegulPolicyContextService.PolicyChunk>
        {
            new("Manual — 6.8 p.15", "Unrelated content.", "Manual.pdf", "6.8", 15),
            new("Manual — 6.2 p.12", "The department has complete independence.", "Manual.pdf", "6.2", 12),
        };

        var judgment = new RegulJudgmentResult
        {
            PolicyExtract = ["Internal audit tests the AML programme annually."],
        };

        var markdownByFile = new Dictionary<string, string> { ["Manual.pdf"] = markdown };
        var result = NdRegulJudgmentPostProcessor.ApplyGroundedDocumentReference(
            judgment,
            chunks,
            markdownByFile);

        Assert.Contains("9.4.1", result.DocumentReference);
        Assert.Contains("p.42", result.DocumentReference);
        Assert.DoesNotContain("6.8", result.DocumentReference);
    }

    [Fact]
    public void ApplyFalseAbsenceCorrection_downgrades_when_audit_section_exists_in_corpus()
    {
        var corpus =
            "6.18 Internal Audit AML Rule 9.4.1: Carrying out an independent assessment to assess AML/CFT compliance.";
        var judgment = new RegulJudgmentResult
        {
            OverallStatus = "non_compliant",
            DesignStatus = "non_compliant",
            OperatingStatus = "non_compliant",
            Confidence = 0.69,
            GapDescription = "The manual does not contain a dedicated Internal Audit section.",
            SuggestedAction = "Add a dedicated Internal Audit section addressing independent audit requirements.",
        };

        var result = NdRegulJudgmentPostProcessor.ApplyFalseAbsenceCorrection(judgment, corpus);

        Assert.Equal("partial", result.OverallStatus);
        Assert.Contains("different section title", result.GapDescription, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Review existing internal audit", result.SuggestedAction, StringComparison.OrdinalIgnoreCase);
    }
}
