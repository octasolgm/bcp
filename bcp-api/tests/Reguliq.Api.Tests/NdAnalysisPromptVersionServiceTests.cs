using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Services.NewDashboard;
using Xunit;

namespace Reguliq.Api.Tests;

public class NdAnalysisPromptVersionServiceTests
{
    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    [Fact]
    public async Task BuildJudgmentContextAsync_uses_current_db_version_not_hardcoded_default()
    {
        await using var db = CreateDb();
        var service = new NdAnalysisPromptVersionService(db);

        db.NdAnalysisPromptVersions.AddRange(
            new NdAnalysisPromptVersion
            {
                PromptKey = NdAnalysisPromptVersionService.JudgmentSystemKey,
                VersionNumber = 1,
                Label = "Base",
                PromptText = "SYSTEM",
                IsCurrent = true,
            },
            new NdAnalysisPromptVersion
            {
                PromptKey = NdAnalysisPromptVersionService.JudgmentUserContextKey,
                VersionNumber = 1,
                Label = "Base",
                PromptText = "CUSTOM CONTEXT:\n{policy_context}\nEND",
                IsCurrent = true,
            },
            new NdAnalysisPromptVersion
            {
                PromptKey = NdAnalysisPromptVersionService.JudgmentUserQueryKey,
                VersionNumber = 1,
                Label = "Base",
                PromptText = "CLAUSE {clause_no}:\n{clause_text}",
                IsCurrent = true,
            });
        await db.SaveChangesAsync();

        var context = await service.BuildJudgmentContextAsync("policy excerpt text", ct: CancellationToken.None);
        var query = await service.BuildJudgmentQueryAsync("2.1.1", "Banks must verify.", ct: CancellationToken.None);
        var system = await service.GetJudgmentSystemPromptAsync(ct: CancellationToken.None);

        Assert.Equal("CUSTOM CONTEXT:\npolicy excerpt text\nEND", context);
        Assert.Equal("CLAUSE 2.1.1:\nBanks must verify.", query);
        Assert.Equal("SYSTEM", system);
    }

    [Fact]
    public async Task SetCurrentAsync_switches_text_used_on_next_build()
    {
        await using var db = CreateDb();
        var service = new NdAnalysisPromptVersionService(db);

        var v1 = new NdAnalysisPromptVersion
        {
            PromptKey = NdAnalysisPromptVersionService.JudgmentUserContextKey,
            VersionNumber = 1,
            Label = "Base",
            PromptText = "V1 {policy_context}",
            IsCurrent = true,
        };
        var v2 = new NdAnalysisPromptVersion
        {
            PromptKey = NdAnalysisPromptVersionService.JudgmentUserContextKey,
            VersionNumber = 2,
            Label = "Custom",
            PromptText = "V2 {policy_context}",
            IsCurrent = false,
        };
        db.NdAnalysisPromptVersions.AddRange(v1, v2);
        await db.SaveChangesAsync();

        Assert.Equal("V1 excerpt", await service.BuildJudgmentContextAsync("excerpt", ct: CancellationToken.None));

        await service.SetCurrentAsync(v2.Id, CancellationToken.None);

        Assert.Equal("V2 excerpt", await service.BuildJudgmentContextAsync("excerpt", ct: CancellationToken.None));
    }

    [Fact]
    public async Task GetCurrentTextAsync_throws_when_no_current_judgment_version()
    {
        await using var db = CreateDb();
        var service = new NdAnalysisPromptVersionService(db);

        db.NdAnalysisPromptVersions.Add(new NdAnalysisPromptVersion
        {
            PromptKey = NdAnalysisPromptVersionService.JudgmentSystemKey,
            VersionNumber = 1,
            Label = "Base",
            PromptText = "SYSTEM",
            IsCurrent = false,
        });
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GetJudgmentSystemPromptAsync(ct: CancellationToken.None));
    }

    [Fact]
    public async Task EnsureJudgmentSemanticV2Async_creates_v2_and_sets_current()
    {
        await using var db = CreateDb();
        var service = new NdAnalysisPromptVersionService(db);

        db.NdAnalysisPromptVersions.Add(new NdAnalysisPromptVersion
        {
            PromptKey = NdAnalysisPromptVersionService.JudgmentSystemKey,
            VersionNumber = 1,
            Label = "Base",
            PromptText = "OLD SYSTEM PROMPT",
            IsCurrent = true,
        });
        await db.SaveChangesAsync();

        await service.EnsureJudgmentSemanticV2Async(CancellationToken.None);

        var versions = await db.NdAnalysisPromptVersions
            .Where(v => v.PromptKey == NdAnalysisPromptVersionService.JudgmentSystemKey)
            .OrderBy(v => v.VersionNumber)
            .ToListAsync(CancellationToken.None);

        Assert.Equal(2, versions.Count);
        Assert.Equal(NdRegulPromptDefaults.JudgmentSemanticV2Label, versions[1].Label);
        Assert.True(versions[1].IsCurrent);
        Assert.False(versions[0].IsCurrent);
    }

    [Fact]
    public async Task EnsureJudgmentSemanticV3Async_creates_v3_and_sets_current()
    {
        await using var db = CreateDb();
        var service = new NdAnalysisPromptVersionService(db);

        db.NdAnalysisPromptVersions.Add(new NdAnalysisPromptVersion
        {
            PromptKey = NdAnalysisPromptVersionService.JudgmentSystemKey,
            VersionNumber = 2,
            Label = "Semantic matching v2",
            PromptText = "OLD AML-SPECIFIC PROMPT with independent audit 9.4.1",
            IsCurrent = true,
        });
        await db.SaveChangesAsync();

        await service.EnsureJudgmentSemanticV3Async(CancellationToken.None);

        var versions = await db.NdAnalysisPromptVersions
            .Where(v => v.PromptKey == NdAnalysisPromptVersionService.JudgmentSystemKey)
            .OrderBy(v => v.VersionNumber)
            .ToListAsync(CancellationToken.None);

        Assert.Equal(2, versions.Count);
        Assert.Equal(NdRegulPromptDefaults.JudgmentSemanticV3Label, versions[1].Label);
        Assert.True(versions[1].IsCurrent);
        Assert.False(versions[0].IsCurrent);
        var system = await service.GetJudgmentSystemPromptAsync(ct: CancellationToken.None);
        Assert.DoesNotContain("9.4.1", system);
        Assert.DoesNotContain("AML-CFT", system);
        Assert.Contains("Semantic matching", system);
    }

    [Fact]
    public async Task EnsureJudgmentFullMarkdownV1Async_creates_v4_prompts_separate_from_v3()
    {
        await using var db = CreateDb();
        var service = new NdAnalysisPromptVersionService(db);

        await service.EnsureJudgmentFullMarkdownV1Async(CancellationToken.None);

        var v3Context = await db.NdAnalysisPromptVersions
            .FirstOrDefaultAsync(v => v.PromptKey == NdAnalysisPromptVersionService.JudgmentUserContextKey && v.IsCurrent);
        var v4Context = await db.NdAnalysisPromptVersions
            .FirstOrDefaultAsync(v => v.PromptKey == NdAnalysisPromptVersionService.JudgmentFullUserContextKey && v.IsCurrent);

        Assert.Null(v3Context);
        Assert.NotNull(v4Context);
        Assert.Equal(NdRegulPromptDefaults.JudgmentFullMarkdownV1Label, v4Context!.Label);
        Assert.Contains("no page limit", v4Context.PromptText, StringComparison.OrdinalIgnoreCase);

        var v4ContextBuilt = await service.BuildJudgmentContextAsync(
            "manual text",
            AnalysisWorkflowEngine.RegulPipelineFull,
            CancellationToken.None);
        Assert.Contains("complete parsed markdown", v4ContextBuilt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("manual text", v4ContextBuilt);

        db.NdAnalysisPromptVersions.Add(new NdAnalysisPromptVersion
        {
            PromptKey = NdAnalysisPromptVersionService.JudgmentUserContextKey,
            VersionNumber = 1,
            Label = "Base",
            PromptText = NdRegulPromptDefaults.JudgmentUserContextTemplate,
            IsCurrent = true,
        });
        await db.SaveChangesAsync(CancellationToken.None);

        var v3ContextBuilt = await service.BuildJudgmentContextAsync(
            "excerpt text",
            AnalysisWorkflowEngine.RegulPipeline,
            CancellationToken.None);
        Assert.Contains("END EXCERPTS", v3ContextBuilt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no page limit", v3ContextBuilt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("excerpt text", v3ContextBuilt);
    }
}
