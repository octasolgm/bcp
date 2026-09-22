using System.Text.Json;

namespace Reguliq.Api.Services.LocalDocs;

public sealed class AzureOpenAIOptions
{
    public string Endpoint { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string EmbeddingDeployment { get; set; } = "text-embedding-3-small";
}

/// <summary>
/// Calls Azure OpenAI Service's embeddings endpoint (hosts OpenAI's own embedding models —
/// text-embedding-3-small at $0.02/1M tokens is the default deployment name here). Used only by
/// semantic extraction, as an alternative embedding source to the free local model
/// (<see cref="LocalEmbeddingService"/>) — the user asked for this specific cloud option, so both exist
/// side by side rather than one replacing the other.
/// </summary>
public sealed class AzureOpenAIEmbeddingClient(HttpClient http, Microsoft.Extensions.Options.IOptions<AzureOpenAIOptions> options)
{
    // Matches the confirmed working deployment URL for this resource — do not bump without
    // re-confirming against the actual Azure OpenAI resource, older resources can reject newer
    // api-version values with a 404.
    private const string ApiVersion = "2023-05-15";

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct)
    {
        var opts = options.Value;
        if (string.IsNullOrWhiteSpace(opts.Endpoint) || string.IsNullOrWhiteSpace(opts.ApiKey))
            throw new InvalidOperationException(
                "Azure OpenAI is not configured (AzureOpenAI:Endpoint / ApiKey).");

        var url = $"openai/deployments/{opts.EmbeddingDeployment}/embeddings?api-version={ApiVersion}";

        using var content = new StringContent(
            JsonSerializer.Serialize(new { input = text }),
            System.Text.Encoding.UTF8,
            "application/json");

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.Add("api-key", opts.ApiKey);

        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Azure OpenAI embeddings returned {(int)response.StatusCode}: {body}");

        using var doc = JsonDocument.Parse(body);
        var embedding = doc.RootElement.GetProperty("data")[0].GetProperty("embedding");
        var vector = new float[embedding.GetArrayLength()];
        var i = 0;
        foreach (var v in embedding.EnumerateArray())
            vector[i++] = v.GetSingle();
        return vector;
    }
}
