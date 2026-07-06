using Reguliq.Api.Services;
using Xunit;

namespace Reguliq.Api.Tests;

public class DashboardServiceTests
{
    [Theory]
    [InlineData("Compliant", "compliant")]
    [InlineData("Partial Compliant", "partial")]
    [InlineData("Non-Compliant", "non-compliant")]
    [InlineData("", "")]
    public void NormalizeStatus_MapsPass2Labels(string input, string expected)
    {
        var method = typeof(DashboardService).GetMethod(
            "NormalizeStatus",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        var result = method.Invoke(null, new object?[] { input }) as string;
        Assert.Equal(expected, result);
    }
}
