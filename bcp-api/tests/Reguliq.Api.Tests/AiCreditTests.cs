using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Reguliq.Api.Data;
using Reguliq.Api.Data.Entities;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Services.NewDashboard.Ai;
using Xunit;

namespace Reguliq.Api.Tests;

public class AiCreditTests
{
    private static readonly Guid Bank = Guid.NewGuid();

    private static AppDbContext CreateDb(string name)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(name)
            .Options;
        return new InMemoryAppDbContext(options);
    }

    /// <summary>The in-memory provider cannot map pgvector columns; nothing here touches embeddings.</summary>
    private sealed class InMemoryAppDbContext(DbContextOptions<AppDbContext> options) : AppDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<NdLocalDocumentExtractionSection>().Ignore(s => s.Embedding);
        }
    }

    [Theory]
    [InlineData(0.09, 0.01, 1.0, 9)]      // a 9 cent call at 1 credit = $0.01
    [InlineData(0.09, 0.01, 2.0, 18)]     // same call with a 2x markup
    [InlineData(0.005, 0.01, 1.0, 0.5)]   // sub-credit calls keep their fraction
    [InlineData(0, 0.01, 1.0, 0)]
    public void Credits_follow_the_configured_rate_and_markup(
        decimal usd, decimal usdPerCredit, decimal markup, decimal expected) =>
        Assert.Equal(expected, new NdAiCreditPricing(usdPerCredit, markup).CreditsFor(usd));

    [Fact]
    public async Task Balance_is_granted_minus_used_and_warns_before_it_runs_out()
    {
        var dbName = Guid.NewGuid().ToString();
        var cache = new MemoryCache(new MemoryCacheOptions());
        await using var db = CreateDb(dbName);
        db.NdWorkspaces.Add(new NdWorkspace { Id = Bank, Name = "Bank", Slug = "bank", AiCreditLowThresholdPct = 20 });
        await db.SaveChangesAsync();

        var service = new NdAiCreditService(db, cache);

        // No grant at all: existing workspaces keep working instead of being locked out.
        var before = await service.GetSummaryAsync(Bank);
        Assert.True(before.Unlimited);
        Assert.False(before.IsExhausted);

        await service.AddCreditsAsync(Bank, 100m, AiCreditKinds.TopUp, null, "first pack");
        var granted = await service.GetSummaryAsync(Bank);
        Assert.Equal(100m, granted.Balance);
        Assert.False(granted.Unlimited);
        Assert.False(granted.IsLow);

        await service.AddCreditsAsync(Bank, -85m, AiCreditKinds.Usage, null, null);
        NdAiCreditService.InvalidateBalance(cache, Bank);
        var low = await service.GetSummaryAsync(Bank);
        Assert.Equal(15m, low.Balance);
        Assert.Equal(85m, low.Used);
        Assert.True(low.IsLow);          // 85% spent, warning line is 80%
        Assert.False(low.IsExhausted);

        await service.AddCreditsAsync(Bank, -15m, AiCreditKinds.Usage, null, null);
        NdAiCreditService.InvalidateBalance(cache, Bank);
        var empty = await service.GetSummaryAsync(Bank);
        Assert.Equal(0m, empty.Balance);
        Assert.True(empty.IsExhausted);
        Assert.False(empty.IsLow);       // exhausted is its own state, not "low"
    }

    [Fact]
    public async Task One_workspaces_credits_are_never_counted_against_another()
    {
        var dbName = Guid.NewGuid().ToString();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var otherBank = Guid.NewGuid();
        await using var db = CreateDb(dbName);
        var service = new NdAiCreditService(db, cache);

        await service.AddCreditsAsync(Bank, 50m, AiCreditKinds.TopUp, null, null);
        await service.AddCreditsAsync(otherBank, 10m, AiCreditKinds.TopUp, null, null);
        await service.AddCreditsAsync(otherBank, -4m, AiCreditKinds.Usage, null, null);

        Assert.Equal(50m, (await service.GetSummaryAsync(Bank)).Balance);
        Assert.Equal(6m, (await service.GetSummaryAsync(otherBank)).Balance);
    }

    [Fact]
    public void OpenRouter_usage_is_read_as_a_real_cost()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://openrouter.ai/api/v1/chat/completions");
        var body = """
        {
          "id": "gen-123",
          "model": "anthropic/claude-sonnet-5",
          "usage": {
            "prompt_tokens": 11500,
            "completion_tokens": 1300,
            "cost": 0.0912,
            "prompt_tokens_details": { "cached_tokens": 900 }
          }
        }
        """;

        var usage = LlmUsageHandler.Parse(request, body);

        Assert.NotNull(usage);
        Assert.Equal("openrouter", usage!.Provider);
        Assert.Equal("anthropic/claude-sonnet-5", usage.Model);
        Assert.Equal(11500, usage.PromptTokens);
        Assert.Equal(1300, usage.CompletionTokens);
        Assert.Equal(900, usage.CachedTokens);
        Assert.Equal(0.0912m, usage.ReportedUsdCost);
        Assert.Equal("gen-123", usage.GenerationId);
    }

    [Fact]
    public void Direct_provider_usage_reports_tokens_with_no_cost_so_it_gets_priced()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
        var body = """
        {"model":"claude-sonnet-5","usage":{"input_tokens":2000,"output_tokens":500,"cache_read_input_tokens":100}}
        """;

        var usage = LlmUsageHandler.Parse(request, body);

        Assert.NotNull(usage);
        Assert.Equal("anthropic", usage!.Provider);
        Assert.Equal(2000, usage.PromptTokens);
        Assert.Equal(500, usage.CompletionTokens);
        Assert.Equal(100, usage.CachedTokens);
        Assert.Null(usage.ReportedUsdCost);
    }

    [Fact]
    public void Responses_without_usage_are_ignored()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
        Assert.Null(LlmUsageHandler.Parse(request, """{"choices":[]}"""));
        Assert.Null(LlmUsageHandler.Parse(request, ""));
    }

    [Fact]
    public void Usage_is_billed_to_the_workspace_in_context_only()
    {
        Assert.Null(NdAiUsageContext.Value);

        using (NdAiUsageContext.Enter(Bank, null, "analysis"))
        {
            Assert.Equal(Bank, NdAiUsageContext.Value!.TenantId);
            Assert.Equal("analysis", NdAiUsageContext.Value.Feature);
        }

        Assert.Null(NdAiUsageContext.Value);
    }
}
