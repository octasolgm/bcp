using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Reguliq.Api.Data;
using Reguliq.Api.Data.Entities;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Services.NewDashboard.Ai;
using Xunit;

namespace Reguliq.Api.Tests;

/// <summary>What we pay a provider vs what the client is charged: markup, credits and margin all agree.</summary>
public class AiCostingTests
{
    private static readonly Guid Bank = Guid.NewGuid();

    private static AppDbContext CreateDb() => new InMemoryAppDbContext(
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed class InMemoryAppDbContext(DbContextOptions<AppDbContext> options) : AppDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<NdLocalDocumentExtractionSection>().Ignore(s => s.Embedding);
        }
    }

    [Theory]
    [InlineData(0.10, 0.01, 1.0, 0.10)]   // no margin: client is charged exactly our cost
    [InlineData(0.10, 0.01, 1.5, 0.15)]   // 1.5x markup: charged 15 cents for a 10 cent call
    [InlineData(0.10, 0.10, 1.5, 0.15)]   // billed value does not depend on how big a credit is
    [InlineData(0, 0.01, 2.0, 0)]
    public void Billed_value_is_cost_times_markup(decimal cost, decimal usdPerCredit, decimal markup, decimal expected) =>
        Assert.Equal(expected, new NdAiCreditPricing(usdPerCredit, markup).BilledUsdFor(cost));

    [Theory]
    [InlineData(0.10, 0.01, 1.5)]
    [InlineData(0.07, 0.02, 2.0)]
    [InlineData(1.00, 0.01, 1.25)]
    public void Credits_times_usd_per_credit_equals_billed_value(decimal cost, decimal usdPerCredit, decimal markup)
    {
        var pricing = new NdAiCreditPricing(usdPerCredit, markup);
        Assert.Equal(pricing.BilledUsdFor(cost), Math.Round(pricing.CreditsFor(cost) * usdPerCredit, 6));
    }

    [Fact]
    public void Price_increase_at_the_provider_keeps_the_same_margin_percentage()
    {
        var pricing = new NdAiCreditPricing(0.01m, 1.5m);
        foreach (var cost in new[] { 0.10m, 0.20m, 0.40m })
        {
            var billed = pricing.BilledUsdFor(cost);
            Assert.Equal(33.3m, Math.Round((billed - cost) / billed * 100m, 1));
        }
    }

    [Fact]
    public async Task A_grant_records_what_the_credits_were_worth_when_given()
    {
        await using var db = CreateDb();
        var service = new NdAiCreditService(db, new MemoryCache(new MemoryCacheOptions()));

        await service.AddCreditsAsync(Bank, 500m, AiCreditKinds.TopUp, null, "$5 pack", default, 0.01m);
        await service.AddCreditsAsync(Bank, 100m, AiCreditKinds.TopUp, null, "no rate given");

        var lines = await db.NdAiCreditLedger.IgnoreQueryFilters().OrderBy(e => e.CreatedAt).ToListAsync();
        Assert.Equal(5m, lines[0].BilledUsd);
        Assert.Null(lines[1].BilledUsd);
    }

    [Theory]
    [InlineData("{\"usdPerCredit\":0.02,\"markup\":1.5,\"minMarginPct\":20}", 0.02, 1.5)]   // what the admin API writes
    [InlineData("{\"UsdPerCredit\":0.05,\"Markup\":2}", 0.05, 2.0)]                            // PascalCase also works
    [InlineData("{\"usdPerCredit\":0,\"markup\":-1}", 0.01, 1.0)]                              // invalid values fall back
    [InlineData("not json", 0.01, 1.0)]
    [InlineData(null, 0.01, 1.0)]
    public void Stored_price_is_read_back_whatever_the_casing(string? json, decimal usdPerCredit, decimal markup)
    {
        var price = NdAiPricingService.ParsePricing(json);
        Assert.Equal(usdPerCredit, price.UsdPerCredit);
        Assert.Equal(markup, price.Markup);
    }

    [Fact]
    public void OpenRouter_response_with_leading_blank_lines_is_still_read_and_carries_the_real_cost()
    {
        // OpenRouter sends keep-alive blank lines before the JSON; this used to make its calls invisible to billing.
        var body = "\n         \n{\"id\":\"gen-1\",\"model\":\"moonshotai/kimi-k3\",\"usage\":{\"prompt_tokens\":16272,\"completion_tokens\":5528,\"cost\":0.1317}}";
        var request = new HttpRequestMessage(HttpMethod.Post, "https://openrouter.ai/api/v1/chat/completions");

        var usage = LlmUsageHandler.Parse(request, body);

        Assert.NotNull(usage);
        Assert.Equal("openrouter", usage!.Provider);
        Assert.Equal("moonshotai/kimi-k3", usage.Model);
        Assert.Equal(16272, usage.PromptTokens);
        Assert.Equal(5528, usage.CompletionTokens);
        Assert.Equal(0.1317m, usage.ReportedUsdCost);
    }

    [Fact]
    public void Anthropic_response_reports_tokens_and_no_cost_so_it_is_estimated_later()
    {
        var body = "{\"model\":\"claude-sonnet-5\",\"usage\":{\"input_tokens\":27506,\"output_tokens\":1813}}";
        var usage = LlmUsageHandler.Parse(
            new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages"), body);

        Assert.NotNull(usage);
        Assert.Equal("anthropic", usage!.Provider);
        Assert.Equal(27506, usage.PromptTokens);
        Assert.Null(usage.ReportedUsdCost);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    public void Bodies_that_are_not_json_objects_are_ignored(string body) =>
        Assert.Null(LlmUsageHandler.Parse(
            new HttpRequestMessage(HttpMethod.Post, "https://openrouter.ai/api/v1/chat/completions"), body));
}
