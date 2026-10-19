using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Reguliq.Api.Services.Llm;
using Xunit;

namespace Reguliq.Api.Tests;

public class OpenRouterPrivacyTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value))
            .Build();

    private sealed class Recorder(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct));
            return new HttpResponseMessage(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }

    private static OpenRouterLlmClient Client(Recorder recorder, params (string Key, string? Value)[] extra) =>
        new(new Factory(recorder),
            Config(extra.Append(("OpenRouter:ApiKey", "test-key")).ToArray()),
            NullLogger<OpenRouterLlmClient>.Instance);

    private const string OkAnswer = """{"choices":[{"finish_reason":"stop","message":{"content":"ok"}}]}""";

    [Fact]
    public void Both_rules_are_on_by_default_and_each_can_be_switched_off()
    {
        var both = LlmPrivacySettings.From(Config()).OpenRouterProviderPreferences()!;
        Assert.Equal(true, both["zdr"]);
        Assert.Equal("deny", both["data_collection"]);

        var onlyTraining = LlmPrivacySettings.From(Config(("Privacy:OpenRouter:ZeroDataRetention", "false"))).OpenRouterProviderPreferences()!;
        Assert.False(onlyTraining.ContainsKey("zdr"));
        Assert.Equal("deny", onlyTraining["data_collection"]);

        var onlyRetention = LlmPrivacySettings.From(Config(("Privacy:OpenRouter:DenyDataCollection", "false"))).OpenRouterProviderPreferences()!;
        Assert.Equal(true, onlyRetention["zdr"]);
        Assert.False(onlyRetention.ContainsKey("data_collection"));

        Assert.Null(LlmPrivacySettings.From(Config(
            ("Privacy:OpenRouter:ZeroDataRetention", "false"),
            ("Privacy:OpenRouter:DenyDataCollection", "false"))).OpenRouterProviderPreferences());
    }

    [Fact]
    public async Task Every_request_carries_the_rules_and_keeps_the_normal_body()
    {
        var recorder = new Recorder(HttpStatusCode.OK, OkAnswer);
        var answer = await Client(recorder).AnalyzeTextAsync("hello", "anthropic/claude-sonnet-5");

        Assert.Equal("ok", answer);
        using var body = JsonDocument.Parse(recorder.Bodies[0]);
        var root = body.RootElement;
        Assert.True(root.GetProperty("provider").GetProperty("zdr").GetBoolean());
        Assert.Equal("deny", root.GetProperty("provider").GetProperty("data_collection").GetString());
        // Nothing else about the request changed.
        Assert.Equal("anthropic/claude-sonnet-5", root.GetProperty("model").GetString());
        Assert.Equal(16384, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal("hello", root.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.False(root.TryGetProperty("reasoning", out _));
    }

    [Fact]
    public async Task Kimi_k3_keeps_its_reasoning_and_token_settings_next_to_the_rules()
    {
        var recorder = new Recorder(HttpStatusCode.OK, OkAnswer);
        await Client(recorder).AnalyzeTextAsync("hello", "moonshotai/kimi-k3");

        using var body = JsonDocument.Parse(recorder.Bodies[0]);
        var root = body.RootElement;
        Assert.Equal("high", root.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal(65536, root.GetProperty("max_tokens").GetInt32());
        Assert.True(root.GetProperty("provider").GetProperty("zdr").GetBoolean());
    }

    [Fact]
    public async Task With_the_rules_switched_off_the_request_is_exactly_as_before()
    {
        var recorder = new Recorder(HttpStatusCode.OK, OkAnswer);
        await Client(recorder,
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
            Client(recorder).AnalyzeTextAsync("hello", "qwen/qwen3.8-max-0902"));

        Assert.Contains("qwen/qwen3.8-max-0902", ex.Message);
        Assert.Contains("data-protection rules", ex.Message);
        Assert.Contains("Privacy:OpenRouter", ex.Message);
        // Not retried without the protection.
        Assert.Single(recorder.Bodies);
    }

    [Fact]
    public async Task Other_OpenRouter_errors_keep_the_normal_message()
    {
        var recorder = new Recorder(HttpStatusCode.InternalServerError, """{"error":{"message":"boom"}}""");
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            Client(recorder).AnalyzeTextAsync("hello", "openai/gpt-5"));
        Assert.Contains("OpenRouter API error", ex.Message);
    }
}
