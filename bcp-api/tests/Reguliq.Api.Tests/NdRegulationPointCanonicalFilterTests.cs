using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Services.NewDashboard;
using Xunit;

namespace Reguliq.Api.Tests;

public class NdRegulationPointCanonicalFilterTests
{
    [Fact]
    public void CountCanonical_excludes_junk_and_duplicate_glossary_rows()
    {
        var docId = Guid.NewGuid();
        var points = new List<NdRegulationPoint>
        {
            new()
            {
                Id = Guid.NewGuid(),
                RegulationDocumentId = docId,
                PointNumber = "3.1.4",
                PointTitle = "Implement management systems",
                PointContent = "Requirement text",
                Status = NdRegulationPointStatus.Active,
            },
            new()
            {
                Id = Guid.NewGuid(),
                RegulationDocumentId = docId,
                PointNumber = "Policies",
                PointTitle = "Policies",
                PointContent = "Glossary duplicate",
                Status = NdRegulationPointStatus.Active,
            },
            new()
            {
                Id = Guid.NewGuid(),
                RegulationDocumentId = docId,
                PointNumber = "3.1.4",
                PointTitle = "Implement management systems",
                PointContent = "Requirement text",
                Status = NdRegulationPointStatus.Active,
            },
        };

        var keep = NdRegulationPointCanonicalFilter.SelectKeepIds(points);
        var count = NdRegulationPointCanonicalFilter.CountCanonical(points);

        Assert.Equal(1, count);
        Assert.DoesNotContain(points[1].Id, keep);
        Assert.Contains(points[0].Id, keep);
    }

    [Fact]
    public void FilterCanonical_preserves_manual_rows()
    {
        var docId = Guid.NewGuid();
        var points = new List<NdRegulationPoint>
        {
            new()
            {
                Id = Guid.NewGuid(),
                RegulationDocumentId = docId,
                PointNumber = "Custom",
                PointTitle = "Manual",
                PointContent = "Manual point",
                Status = NdRegulationPointStatus.Active,
            },
        };

        var filtered = NdRegulationPointCanonicalFilter.FilterCanonical(points, isManual: true);

        Assert.Single(filtered);
    }
}
