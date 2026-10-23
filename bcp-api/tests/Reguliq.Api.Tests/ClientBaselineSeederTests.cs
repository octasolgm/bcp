using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Reguliq.Api.Data;
using Reguliq.Api.Data.Entities;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Infrastructure;
using Xunit;

namespace Reguliq.Api.Tests;

public class ClientBaselineSeederTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "client-baseline-" + Guid.NewGuid().ToString("N"));

    public ClientBaselineSeederTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

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
        new InMemoryAppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static IConfiguration Config(bool enabled) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ClientBaseline:Enabled"] = enabled ? "true" : "false" })
            .Build();

    private Task ApplyAsync(AppDbContext db, bool enabled = true) =>
        ClientBaselineSeeder.ApplyAsync(
            db, Config(enabled), new TestWebHostEnvironment { ContentRootPath = _dir }, NullLogger.Instance, CancellationToken.None);

    private void WriteBaseline(string promptText, string model) =>
        File.WriteAllText(Path.Combine(_dir, ClientBaselineSeeder.FileName), $$"""
            {
              "settings": {
                "regul_workflow_llm": { "Provider": "openrouter", "Model": "{{model}}" },
                "regul_pipeline_version": 6
              },
              "prompts": [ { "key": "regul_judgment_system", "versionNumber": 11, "label": "v11", "text": "{{promptText}}" } ]
            }
            """);

    [Fact]
    public async Task Fresh_database_gets_live_prompts_and_settings()
    {
        WriteBaseline("live prompt", "moonshotai/kimi-k3");
        await using var db = CreateDb();

        await ApplyAsync(db);

        var prompt = await db.NdAnalysisPromptVersions.SingleAsync(v => v.PromptKey == "regul_judgment_system");
        Assert.True(prompt.IsCurrent);
        Assert.Equal(11, prompt.VersionNumber);
        Assert.Equal("live prompt", prompt.PromptText);
        Assert.Equal("6", (await db.NdSystemSettings.SingleAsync(s => s.Key == "regul_pipeline_version")).ValueJson);
        Assert.Contains("kimi-k3", (await db.NdSystemSettings.SingleAsync(s => s.Key == "regul_workflow_llm")).ValueJson);
    }

    [Fact]
    public async Task Patch_replaces_system_versions_but_keeps_what_the_client_admin_changed()
    {
        var admin = Guid.NewGuid();
        await using var db = CreateDb();
        db.NdAnalysisPromptVersions.Add(new NdAnalysisPromptVersion
        {
            PromptKey = "regul_judgment_system", VersionNumber = 1, Label = "Base", PromptText = "base", IsCurrent = true,
        });
        db.NdSystemSettings.Add(new NdSystemSetting { Key = "regul_pipeline_version", ValueJson = "5" });
        db.NdSystemSettings.Add(new NdSystemSetting
        {
            Key = "regul_workflow_llm", ValueJson = """{"Provider":"openrouter","Model":"client/own-model"}""", UpdatedBy = admin,
        });
        await db.SaveChangesAsync();
        WriteBaseline("live prompt", "moonshotai/kimi-k3");

        await ApplyAsync(db);
        await ApplyAsync(db); // a second start changes nothing

        var versions = await db.NdAnalysisPromptVersions.OrderBy(v => v.VersionNumber).ToListAsync();
        Assert.Equal(2, versions.Count);
        Assert.False(versions[0].IsCurrent);
        Assert.True(versions[1].IsCurrent);
        Assert.Equal((2, "live prompt"), (versions[1].VersionNumber, versions[1].PromptText));
        Assert.Equal("6", (await db.NdSystemSettings.SingleAsync(s => s.Key == "regul_pipeline_version")).ValueJson);
        Assert.Contains("client/own-model", (await db.NdSystemSettings.SingleAsync(s => s.Key == "regul_workflow_llm")).ValueJson);

        // The client's admin makes their own prompt current; the next patch leaves it current.
        versions[1].IsCurrent = false;
        db.NdAnalysisPromptVersions.Add(new NdAnalysisPromptVersion
        {
            PromptKey = "regul_judgment_system", VersionNumber = 3, Label = "Ours", PromptText = "client prompt",
            IsCurrent = true, CreatedBy = admin,
        });
        await db.SaveChangesAsync();
        WriteBaseline("newer live prompt", "moonshotai/kimi-k3");

        await ApplyAsync(db);

        var current = await db.NdAnalysisPromptVersions.SingleAsync(v => v.IsCurrent);
        Assert.Equal("client prompt", current.PromptText);
        Assert.True(await db.NdAnalysisPromptVersions.AnyAsync(v => v.PromptText == "newer live prompt"));
    }

    [Fact]
    public async Task Does_nothing_unless_enabled()
    {
        WriteBaseline("live prompt", "moonshotai/kimi-k3");
        await using var db = CreateDb();

        await ApplyAsync(db, enabled: false);

        Assert.False(await db.NdAnalysisPromptVersions.AnyAsync());
        Assert.False(await db.NdSystemSettings.AnyAsync());
    }
}
