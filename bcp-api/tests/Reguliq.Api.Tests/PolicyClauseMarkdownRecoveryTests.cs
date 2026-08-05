using Reguliq.Api.Services.LandingAi;
using Xunit;

namespace Reguliq.Api.Tests;

public class PolicyClauseMarkdownRecoveryTests
{
    [Fact]
    public void MergeMissing_adds_section_from_markdown_when_extract_missed_it()
    {
        var extracted = new List<PolicyClause>
        {
            new("6.2", "Independence of the Compliance Department. The department has complete independence.", 0),
        };

        var markdown = """
            6.2 Independence of the Compliance Department.
            The department has complete independence in relation to investigations.

            9.4.1 Internal Audit Review
            The internal audit function shall periodically test the AML/CFT programme including subsidiaries.
            """;

        var merged = PolicyClauseMarkdownRecovery.MergeMissing(extracted, markdown);

        Assert.Contains(merged, c => c.ClauseNo == "9.4.1");
        Assert.Contains(merged, c => c.ClauseText.Contains("internal audit", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void MergeMissing_does_not_duplicate_existing_clause()
    {
        var extracted = new List<PolicyClause>
        {
            new("9.4.1", "Internal audit text from Landing extract.", 0),
        };

        var markdown = """
            9.4.1 Internal Audit Review
            Different markdown body that should not create a duplicate.
            """;

        var merged = PolicyClauseMarkdownRecovery.MergeMissing(extracted, markdown);
        Assert.Single(merged);
    }
}
