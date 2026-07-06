using Reguliq.Api.Services;
using Xunit;

namespace Reguliq.Api.Tests;

public class GovPointsServiceTests
{
    [Fact]
    public void ResolveSelectedPoints_FindsExpandedLeafIds()
    {
        var service = new GovPointsService(
            new TestWebHostEnvironment(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GovPointsService>.Instance);

        var resolved = service.ResolveSelectedPoints(["3.1.1", "3.1.2"], "leaf");

        Assert.Equal(2, resolved.Count);
        Assert.Contains(resolved, p => p.PointId == "3.1.1");
        Assert.Contains(resolved, p => p.PointId == "3.1.2");
        Assert.All(resolved, p => Assert.False(string.IsNullOrWhiteSpace(p.Text)));
    }

    [Fact]
    public void ResolveSelectedPoints_UsesClientPointsWhenProvided()
    {
        var service = new GovPointsService(
            new TestWebHostEnvironment(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GovPointsService>.Instance);

        var client = new[]
        {
            new Reguliq.Api.Models.GovPoint("3.1.1", "Test", "must comply with screening", "3.1."),
        };

        var resolved = service.ResolveSelectedPoints(["3.1.1"], "leaf", client);

        Assert.Single(resolved);
        Assert.Equal("must comply with screening", resolved[0].Text);
    }
}
