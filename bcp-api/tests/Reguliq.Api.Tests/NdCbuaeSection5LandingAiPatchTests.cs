using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Services.NewDashboard;
using Xunit;

namespace Reguliq.Api.Tests;

public class NdCbuaeSection5LandingAiPatchTests
{
    [Fact]
    public void ApplyMissing_inserts_section_5_rows_when_absent()
    {
        var apiRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var patch = new NdCbuaeSection5LandingAiPatch(new HostEnvironmentStub(apiRoot));

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using var db = new AppDbContext(options);

        var docId = Guid.NewGuid();
        var active = new List<NdRegulationPoint>
        {
            new()
            {
                Id = Guid.NewGuid(),
                RegulationDocumentId = docId,
                PointNumber = "4.2.2",
                PointContent = "x",
                Status = NdRegulationPointStatus.Active,
            },
        };

        var added = patch.ApplyMissing(docId, active, db);

        Assert.Equal(5, added);
        Assert.Equal(5, db.NdRegulationPoints.Local.Count);
        Assert.Contains(db.NdRegulationPoints.Local, p => p.PointNumber == "5.1");
    }

    private sealed class HostEnvironmentStub(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = contentRoot;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
