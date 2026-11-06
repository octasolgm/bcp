using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Reguliq.Api.Data;
using Reguliq.Api.Data.Entities;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Services.NewDashboard;
using Reguliq.Api.Services.NewDashboard.Ai;
using Reguliq.Api.Services.NewDashboard.CorrectedDocs;
using Xunit;

namespace Reguliq.Api.Tests;

public class ClauseLimitAndFinalizeEmbedTests
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
    public void No_clause_limit_always_fits()
    {
        var usage = new NdClauseUsage(null, 5000);
        Assert.False(usage.HasLimit);
        Assert.False(usage.IsReached);
        Assert.Null(usage.Remaining);
        Assert.True(usage.Fits(10_000));
    }

    [Theory]
    [InlineData(100, 60, 40, true)]   // exactly what is left
    [InlineData(100, 60, 41, false)]  // one clause too many
    [InlineData(100, 100, 0, true)]   // nothing new to run
    [InlineData(100, 100, 1, false)]  // limit reached
    public void Clause_limit_allows_a_run_only_when_it_fits(int limit, int used, int toRun, bool fits) =>
        Assert.Equal(fits, new NdClauseUsage(limit, used).Fits(toRun));

    [Fact]
    public async Task Clause_usage_counts_processed_clauses_of_this_workspace_only()
    {
        var bank = Guid.NewGuid();
        var other = Guid.NewGuid();
        await using var db = CreateDb();
        db.NdWorkspaces.Add(new NdWorkspace { Id = bank, Name = "Bank", Slug = "bank", ClauseLimit = 50 });
        db.NdWorkspaces.Add(new NdWorkspace { Id = other, Name = "Other", Slug = "other" });
        db.NdAnalysisRuns.Add(new NdAnalysisRun { Name = "a", TenantId = bank, TotalPointsCount = 20, ProcessedPointsCount = 20 });
        db.NdAnalysisRuns.Add(new NdAnalysisRun { Name = "b", TenantId = bank, TotalPointsCount = 30, ProcessedPointsCount = 12, Status = "deleted" });
        db.NdAnalysisRuns.Add(new NdAnalysisRun { Name = "draft", TenantId = bank, TotalPointsCount = 40 });
        db.NdAnalysisRuns.Add(new NdAnalysisRun { Name = "c", TenantId = other, TotalPointsCount = 99, ProcessedPointsCount = 99 });
        await db.SaveChangesAsync();

        var usage = await new NdAiCreditService(db, new MemoryCache(new MemoryCacheOptions())).GetClauseUsageAsync(bank);

        Assert.Equal(50, usage.Limit);
        Assert.Equal(32, usage.Used);
        Assert.Equal(18, usage.Remaining);
        Assert.False(usage.Fits(40));
    }

    [Theory]
    [InlineData(null, 500, true)] // no per-analysis limit
    [InlineData(10, 10, true)]    // exactly the limit
    [InlineData(10, 11, false)]   // one clause too many
    [InlineData(10, 1, true)]
    public void Per_analysis_limit_allows_only_analyses_within_it(int? perAnalysis, int clauses, bool fits) =>
        Assert.Equal(fits, new NdClauseUsage(100, 0, perAnalysis).FitsOneAnalysis(clauses));

    [Fact]
    public async Task Clause_usage_carries_the_per_analysis_limit()
    {
        var bank = Guid.NewGuid();
        await using var db = CreateDb();
        db.NdWorkspaces.Add(new NdWorkspace { Id = bank, Name = "Bank", Slug = "bank", ClauseLimit = 100, MaxClausesPerAnalysis = 10 });
        await db.SaveChangesAsync();

        var usage = await new NdAiCreditService(db, new MemoryCache(new MemoryCacheOptions())).GetClauseUsageAsync(bank);

        Assert.Equal(100, usage.Limit);
        Assert.Equal(10, usage.PerAnalysisLimit);
        Assert.True(usage.FitsOneAnalysis(10));
        Assert.False(usage.FitsOneAnalysis(11));
    }

    private static NdActionPlanEmbedTarget Target(Guid docId, int? page, string action = "Amend the manual to define PEPs.") =>
        new(docId, page, "anchor", "6.1.1", "Customer due diligence", "PEP definition missing.", action,
            "Compliance", DateTimeOffset.UtcNow, "Reviewer", GapIndex: 1);

    [Fact]
    public void Same_clause_gap_action_in_two_documents_shares_one_policy_text() =>
        Assert.Equal(
            NdCorrectedDocumentService.EmbedBodyKey(Target(Guid.NewGuid(), 4)),
            NdCorrectedDocumentService.EmbedBodyKey(Target(Guid.NewGuid(), 17)));

    [Fact]
    public void A_different_action_on_the_same_gap_gets_its_own_policy_text() =>
        Assert.NotEqual(
            NdCorrectedDocumentService.EmbedBodyKey(Target(Guid.NewGuid(), 4)),
            NdCorrectedDocumentService.EmbedBodyKey(Target(Guid.NewGuid(), 4, "Add a PEP screening control.")));
}
