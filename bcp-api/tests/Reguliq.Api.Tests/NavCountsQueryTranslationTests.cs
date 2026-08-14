using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Xunit;

namespace Reguliq.Api.Tests;

/// <summary>
/// Guards the shapes the sidebar nav-counts endpoint relies on. A translation failure there only
/// shows up as a runtime 500, so the SQL is generated here instead (no connection required).
/// </summary>
public class NavCountsQueryTranslationTests
{
    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=bcp_test;Username=postgres;Password=postgres")
            .Options);

    [Fact]
    public void RunStatusTally_IncludingDeletedBin_TranslatesToSql()
    {
        using var db = CreateContext();

        var sql = db.NdAnalysisRuns.AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new
            {
                All = g.Count(r => r.Status != "deleted"),
                Correction = g.Count(r => r.Status == "pulled_back"),
                Checker = g.Count(r => r.Status == "submitted_for_review"),
                Reviewer = g.Count(r => r.Status == "checker_approved"),
                Deleted = g.Count(r => r.Status == "deleted"),
            })
            .ToQueryString();

        Assert.Contains("SELECT", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StoredDocumentBins_TranslateToOneGroupedStatement()
    {
        using var db = CreateContext();

        var sql = db.StoredDocuments.AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Internal = g.Count(d =>
                    (d.DocKind == "document" || d.DocKind == "internal") && !d.IsHidden),
                InternalDeleted = g.Count(d =>
                    (d.DocKind == "document" || d.DocKind == "internal") && d.IsHidden),
                RegulationDeleted = g.Count(d => d.DocKind == "regulation" && d.IsHidden),
            })
            .ToQueryString();

        Assert.Contains("SELECT", sql, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Documents why the counters are run as parallel queries rather than as subqueries in one
    /// projection: a captured IQueryable is evaluated client-side (it executes immediately)
    /// instead of composing into SQL, which would defeat the batching and hit the database anyway.
    /// </summary>
    [Fact]
    public void CapturedQueryableInProjection_IsNotComposedIntoSql()
    {
        using var db = CreateContext();
        var libraries = db.NdLibraries.AsNoTracking();

        // Would need a live connection because Count() runs client-side during parameterization.
        Assert.ThrowsAny<Exception>(() =>
            db.NdProfiles.AsNoTracking()
                .Select(_ => new { Libraries = libraries.Count() })
                .ToQueryString());
    }
}
