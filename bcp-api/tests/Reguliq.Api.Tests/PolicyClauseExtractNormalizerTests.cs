using Reguliq.Api.Services.LandingAi;
using Xunit;

namespace Reguliq.Api.Tests;

public class PolicyClauseExtractNormalizerTests
{
    [Theory]
    [InlineData("1.", 1, "1.1")]
    [InlineData("1.", 2, "1.2")]
    [InlineData("6.2", 1, "6.2.1")]
    [InlineData("9.4.1", 1, "9.4.1.1")]
    public void BuildDedupedClauseNo_uses_dotted_suffix_not_dash(string clauseNo, int index, string expected)
    {
        Assert.Equal(expected, PolicyClauseExtractNormalizer.BuildDedupedClauseNo(clauseNo, index));
    }

    [Fact]
    public void DedupeClauseNumbers_avoids_1_dot_dash_1_ids()
    {
        var clauses = new List<PolicyClause>
        {
            new("1.", "First", 1),
            new("1.", "Second", 2),
        };

        var result = PolicyClauseExtractNormalizer.DedupeClauseNumbers(clauses);

        Assert.Equal("1.", result[0].ClauseNo);
        Assert.Equal("1.1", result[1].ClauseNo);
    }
}
