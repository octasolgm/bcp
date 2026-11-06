using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.Entities;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Services.NewDashboard;
using Xunit;

namespace Reguliq.Api.Tests;

/// <summary>A maker sees their own analyses plus those shared (Visible to) with one of their departments.</summary>
public class MakerDepartmentVisibilityTests
{
    private sealed class InMemoryAppDbContext(DbContextOptions<AppDbContext> options) : AppDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<NdLocalDocumentExtractionSection>().Ignore(s => s.Embedding);
            modelBuilder.Entity<NdLocalDocumentPassage>().Ignore(p => p.Embedding);
        }
    }

    private static AppDbContext CreateDb() =>
        new InMemoryAppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    [Fact]
    public async Task Maker_sees_own_and_department_shared_runs_only()
    {
        var maker = Guid.NewGuid();
        var other = Guid.NewGuid();
        var compliance = Guid.NewGuid();
        var finance = Guid.NewGuid();
        await using var db = CreateDb();
        db.NdDepartments.AddRange(
            new NdDepartment { Id = compliance, Name = "Compliance" },
            new NdDepartment { Id = finance, Name = "Finance" });
        var own = new NdAnalysisRun { Name = "own", CreatedBy = maker };
        var sharedWithMine = new NdAnalysisRun { Name = "shared", CreatedBy = other };
        var sharedElsewhere = new NdAnalysisRun { Name = "finance only", CreatedBy = other };
        var allDepartments = new NdAnalysisRun { Name = "all departments", CreatedBy = other };
        db.NdAnalysisRuns.AddRange(own, sharedWithMine, sharedElsewhere, allDepartments);
        db.NdAnalysisRunDepartments.AddRange(
            new NdAnalysisRunDepartment { AnalysisRunId = sharedWithMine.Id, DepartmentId = compliance },
            new NdAnalysisRunDepartment { AnalysisRunId = sharedElsewhere.Id, DepartmentId = finance });
        await db.SaveChangesAsync();

        var scope = new NdDepartmentScope(false, maker, [compliance], [compliance]);
        var names = await db.NdAnalysisRuns.ApplyToRuns(db, scope).ApplyMakerVisibility(db, scope)
            .Select(r => r.Name).OrderBy(n => n).ToListAsync();

        Assert.Equal(["own", "shared"], names);
    }

    [Fact]
    public async Task Maker_without_departments_sees_only_own_runs()
    {
        var maker = Guid.NewGuid();
        await using var db = CreateDb();
        db.NdAnalysisRuns.AddRange(
            new NdAnalysisRun { Name = "own", CreatedBy = maker },
            new NdAnalysisRun { Name = "someone else", CreatedBy = Guid.NewGuid() });
        await db.SaveChangesAsync();

        var scope = new NdDepartmentScope(false, maker, [], []);
        var names = await db.NdAnalysisRuns.ApplyMakerVisibility(db, scope).Select(r => r.Name).ToListAsync();

        Assert.Equal(["own"], names);
    }
}
