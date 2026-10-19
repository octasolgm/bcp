using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Reguliq.Api.Services.Llm;
using Xunit;

namespace Reguliq.Api.Tests;

public class PrivacySettingsTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value))
            .Build();

    [Fact]
    public void Defaults_are_the_strict_ones_and_nothing_is_blocked()
    {
        var s = LlmPrivacySettings.From(Config());
        Assert.True(s.OpenRouterZeroDataRetention);
        Assert.True(s.OpenRouterDenyDataCollection);
        Assert.False(s.DisableQualitativeAssessment);
        Assert.False(s.OutboundRestricted);
        Assert.Null(s.CheckOutbound(new Uri("https://anything.example/x")));
    }

    [Fact]
    public void OpenRouter_preferences_follow_the_two_switches()
    {
        var both = LlmPrivacySettings.From(Config()).OpenRouterProviderPreferences();
        Assert.Equal(true, both!["zdr"]);
        Assert.Equal("deny", both["data_collection"]);

        var onlyTraining = LlmPrivacySettings.From(Config(("Privacy:OpenRouter:ZeroDataRetention", "false"))).OpenRouterProviderPreferences();
        Assert.False(onlyTraining!.ContainsKey("zdr"));
        Assert.Equal("deny", onlyTraining["data_collection"]);

        var off = LlmPrivacySettings.From(Config(
            ("Privacy:OpenRouter:ZeroDataRetention", "false"),
            ("Privacy:OpenRouter:DenyDataCollection", "false"))).OpenRouterProviderPreferences();
        Assert.Null(off);
    }

    [Fact]
    public void An_allow_list_blocks_other_hosts_but_always_allows_this_machine()
    {
        var s = LlmPrivacySettings.From(Config(
            ("Privacy:AllowedOutboundHosts:0", "openrouter.ai"),
            ("Privacy:AllowedOutboundHosts:1", "*.cognitiveservices.azure.com")));

        Assert.Null(s.CheckOutbound(new Uri("https://openrouter.ai/api/v1/chat/completions")));
        Assert.Null(s.CheckOutbound(new Uri("https://bank-di.cognitiveservices.azure.com/documentintelligence")));
        Assert.Null(s.CheckOutbound(new Uri("http://localhost:5100/health")));
        Assert.Null(s.CheckOutbound(new Uri("http://127.0.0.1:54321/x")));

        Assert.NotNull(s.CheckOutbound(new Uri("https://api.openai.com/v1/chat/completions")));
        Assert.NotNull(s.CheckOutbound(new Uri("https://evil-openrouter.ai.example/x")));
        Assert.NotNull(s.CheckOutbound(new Uri("https://cognitiveservices.azure.com.evil.example/x")));
    }

    [Fact]
    public void The_https_rule_blocks_plain_http_except_for_listed_hosts()
    {
        var s = LlmPrivacySettings.From(Config(
            ("Privacy:RequireHttpsOutbound", "true"),
            ("Privacy:AllowInsecureHosts:0", "supabase.bank.internal")));

        Assert.NotNull(s.CheckOutbound(new Uri("http://api.example.com/x")));
        Assert.Null(s.CheckOutbound(new Uri("https://api.example.com/x")));
        Assert.Null(s.CheckOutbound(new Uri("http://supabase.bank.internal/storage")));
        Assert.Null(s.CheckOutbound(new Uri("http://localhost:8000/x")));
    }

    private sealed class Recorder(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Bodies.Add(request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct));
            return new HttpResponseMessage(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task The_outbound_handler_stops_a_blocked_call_before_it_is_sent()
    {
        var inner = new Recorder(HttpStatusCode.OK, "{}");
        var handler = new OutboundPolicyHandler(
            Config(("Privacy:AllowedOutboundHosts:0", "openrouter.ai")),
            NullLogger<OutboundPolicyHandler>.Instance)
        { InnerHandler = inner };
        using var http = new HttpClient(handler);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync("https://api.openai.com/v1/models"));
        Assert.Contains("Privacy:AllowedOutboundHosts", ex.Message);
        Assert.Equal(0, inner.Calls);

        using var ok = await http.GetAsync("https://openrouter.ai/api/v1/models");
        Assert.Equal(1, inner.Calls);
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }

    private static OpenRouterLlmClient OpenRouter(Recorder recorder, params (string Key, string? Value)[] extra) =>
        new(new Factory(recorder),
            Config(extra.Append(("OpenRouter:ApiKey", "test-key")).ToArray()),
            NullLogger<OpenRouterLlmClient>.Instance);

    private const string OkAnswer = """{"choices":[{"finish_reason":"stop","message":{"content":"ok"}}]}""";

    [Fact]
    public async Task Every_OpenRouter_request_carries_the_zero_retention_and_no_training_rules()
    {
        var recorder = new Recorder(HttpStatusCode.OK, OkAnswer);
        var answer = await OpenRouter(recorder).AnalyzeTextAsync("hello", "moonshotai/kimi-k3");

        Assert.Equal("ok", answer);
        using var body = JsonDocument.Parse(recorder.Bodies[0]);
        var provider = body.RootElement.GetProperty("provider");
        Assert.True(provider.GetProperty("zdr").GetBoolean());
        Assert.Equal("deny", provider.GetProperty("data_collection").GetString());
        // The model's own settings are still sent.
        Assert.True(body.RootElement.TryGetProperty("reasoning", out _));
    }

    [Fact]
    public async Task The_rules_can_be_switched_off_and_then_no_provider_block_is_sent()
    {
        var recorder = new Recorder(HttpStatusCode.OK, OkAnswer);
        await OpenRouter(recorder,
            ("Privacy:OpenRouter:ZeroDataRetention", "false"),
            ("Privacy:OpenRouter:DenyDataCollection", "false")).AnalyzeTextAsync("hello", "openai/gpt-5");

        using var body = JsonDocument.Parse(recorder.Bodies[0]);
        Assert.False(body.RootElement.TryGetProperty("provider", out _));
    }

    [Fact]
    public async Task A_model_no_endpoint_serves_under_the_rules_gets_a_plain_explanation()
    {
        var recorder = new Recorder(HttpStatusCode.NotFound,
            """{"error":{"message":"No endpoints found matching your data policy (Zero data retention)","code":404}}""");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            OpenRouter(recorder).AnalyzeTextAsync("hello", "qwen/qwen3.8-max-0902"));

        Assert.Contains("qwen/qwen3.8-max-0902", ex.Message);
        Assert.Contains("data-protection rules", ex.Message);
        Assert.Contains("Privacy:OpenRouter", ex.Message);
    }

    [Fact]
    public async Task Other_OpenRouter_errors_keep_the_normal_message()
    {
        var recorder = new Recorder(HttpStatusCode.InternalServerError, """{"error":{"message":"boom"}}""");
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            OpenRouter(recorder).AnalyzeTextAsync("hello", "openai/gpt-5"));
        Assert.Contains("OpenRouter API error", ex.Message);
    }
}
