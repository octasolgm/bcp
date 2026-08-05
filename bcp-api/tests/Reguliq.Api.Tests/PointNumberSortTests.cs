using Reguliq.Api.Services;
using Xunit;

namespace Reguliq.Api.Tests;

public class PointNumberSortTests
{
    [Theory]
    [InlineData("6.18-a", "6.18-b", -1)]
    [InlineData("6.18-b", "6.18-a", 1)]
    [InlineData("6.18", "6.18.1", -1)]
    [InlineData("8.4", "8.10", -1)]
    [InlineData("9.4.1", "14.4", -1)]
    [InlineData("§8.4", "8.4", 0)]
    public void Compare_orders_section_refs_naturally(string left, string right, int expectedSign)
    {
        var cmp = PointNumberSort.Compare(left, right);
        Assert.Equal(Math.Sign(expectedSign), Math.Sign(cmp));
    }
}
