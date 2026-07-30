using Reguliq.Api.Models;
using Reguliq.Api.Services.NewDashboard;
using Xunit;

namespace Reguliq.Api.Tests;

public class GovPointExtractNormalizerTests
{
    [Fact]
    public void DedupeAndFilter_keeps_longest_text_per_point_number()
    {
        var points = new List<GovPoint>
        {
            new("1.1", "Purpose and Scope", "Short", "Intro", 1),
            new("1.1", "Purpose and Scope", new string('x', 200), "Intro", 1),
            new("3.1.4", "Implement management systems", "Brief;", "3.1", 12),
        };

        var result = GovPointExtractNormalizer.DedupeAndFilter(points);

        Assert.Equal(2, result.Count);
        Assert.Equal(200, result.Single(p => p.PointId == "1.1").Text.Length);
    }

    [Fact]
    public void DedupeAndFilter_nests_distinct_siblings_with_same_number()
    {
        var points = new List<GovPoint>
        {
            new("7.8", "Establish policies", "Policy text A", "7.8 Confidentiality", 1),
            new("7.8", "Confidentiality when reporting", "Policy text B", "7.8 Confidentiality", 1),
            new("7.8", "Ensure STR information confidentiality", "Policy text C", "7.8 Confidentiality", 1),
            new("7.8", "Prohibition against Tipping Off", "Policy text D", "7.8 Confidentiality", 1),
        };

        var result = GovPointExtractNormalizer.DedupeAndFilter(points);

        Assert.Equal(
            new[] { "7.8.1", "7.8.2", "7.8.3", "7.8.4" },
            result.Select(p => p.PointId).OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void IsJunkExtractPointId_rejects_structural_headings()
    {
        Assert.True(GovPointExtractNormalizer.IsJunkExtractPointId("Part III"));
        Assert.True(GovPointExtractNormalizer.IsJunkExtractPointId("Elements of an AML/CFT Program"));
        Assert.True(GovPointExtractNormalizer.IsJunkExtractPointId("AML/CFT Program Element 1"));
        Assert.False(GovPointExtractNormalizer.IsJunkExtractPointId("3.1.4"));
        Assert.False(GovPointExtractNormalizer.IsJunkExtractPointId("AML-CFT Law Article 16.1"));
    }

    [Fact]
    public void PlanRepair_soft_deletes_duplicates_and_junk()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var id3 = Guid.NewGuid();
        var rows = new[]
        {
            new Row(id1, "1.1", "A", new string('b', 50), 1),
            new Row(id2, "1.1", "A", new string('c', 10), 1),
            new Row(id3, "Part III", "Elements", "text", 1),
        };

        var plan = GovPointExtractNormalizer.PlanRepair(
            rows,
            r => r.Id,
            r => r.Number,
            r => r.Title,
            r => r.Content,
            r => r.Page);

        Assert.Contains(id2, plan.SoftDeleteIds);
        Assert.Contains(id3, plan.SoftDeleteIds);
        Assert.Contains(id1, plan.KeepIds);
        Assert.Empty(plan.RenumberTo);
    }

    [Fact]
    public void PlanRepair_renumbers_distinct_duplicate_numbers()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var id3 = Guid.NewGuid();
        var rows = new[]
        {
            new Row(id1, "7.8", "Establish policies", "text A", 1),
            new Row(id2, "7.8", "Confidentiality when reporting", "text B", 1),
            new Row(id3, "7.8", "Prohibition against Tipping Off", "text C", 1),
        };

        var plan = GovPointExtractNormalizer.PlanRepair(
            rows,
            r => r.Id,
            r => r.Number,
            r => r.Title,
            r => r.Content,
            r => r.Page);

        Assert.Equal(3, plan.KeepIds.Count);
        Assert.Empty(plan.SoftDeleteIds);
        Assert.Equal("7.8.1", plan.RenumberTo[id1]);
        Assert.Equal("7.8.2", plan.RenumberTo[id2]);
        Assert.Equal("7.8.3", plan.RenumberTo[id3]);
    }

    [Fact]
    public void SynthesizeMissingParentPoints_creates_parent_from_child_section()
    {
        var points = new List<GovPoint>
        {
            new("3.1.1", "Identify risks", "LFIs must identify…", "3.1. Summary of Minimum Statutory Obligations", 12),
            new("3.1.2", "Define scope", "LFIs must define…", "3.1. Summary of Minimum Statutory Obligations", 12),
        };

        var result = GovPointExtractNormalizer.SynthesizeMissingParentPoints(points);

        var parent = result.Single(p => p.PointId == "3.1");
        Assert.Equal("Summary of Minimum Statutory Obligations", parent.Title);
        Assert.Equal("informational", parent.PointType);
    }

    [Fact]
    public void IsValidExtractPointId_allows_named_callouts()
    {
        Assert.True(GovPointExtractNormalizer.IsValidExtractPointId("FIU Instructions"));
    }

    private sealed record Row(Guid Id, string Number, string Title, string Content, int? Page);
}
