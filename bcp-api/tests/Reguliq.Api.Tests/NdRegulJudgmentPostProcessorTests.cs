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
    }
}
