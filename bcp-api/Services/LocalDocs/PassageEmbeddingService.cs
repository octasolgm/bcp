using Microsoft.Extensions.Options;

namespace Reguliq.Api.Services.LocalDocs;

public sealed class RegulRetrievalOptions
{
    /// <summary>Embedding model for search passages (pipeline v4+): "local" (bge-micro-v2 on this server, free,
    /// the default) or "azure-openai" (the AzureOpenAI:EmbeddingDeployment model, e.g. text-embedding-3-small).
    /// Changing it needs a re-index: passages embedded with another model are re-embedded when next used.</summary>
    public string EmbeddingProvider { get; set; } = "local";
}

/// <summary>
/// One place that turns passage and query text into vectors for retrieval pipeline v4+, so the index and the
/// queries always use the same model. <see cref="ModelName"/> is stored on every passage; vectors of different
/// models are never compared.
/// </summary>
public sealed class PassageEmbeddingService(
    LocalEmbeddingService local,
    AzureOpenAIEmbeddingClient azure,
    IOptions<AzureOpenAIOptions> azureOptions,
    IOptions<RegulRetrievalOptions> options)
{
    private const int AzureConcurrency = 8;

    public bool UsesAzure =>
        string.Equals(options.Value.EmbeddingProvider?.Trim(), "azure-openai", StringComparison.OrdinalIgnoreCase);

    public string ModelName => UsesAzure
        ? $"azure-openai:{azureOptions.Value.EmbeddingDeployment}"
        : "local:bge-micro-v2";

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct) =>
        UsesAzure ? await azure.EmbedAsync(text, ct) : local.Embed(text);

    public async Task<float[][]> EmbedManyAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        var vectors = new float[texts.Count][];
        if (!UsesAzure)
        {
            for (var i = 0; i < texts.Count; i++) vectors[i] = local.Embed(texts[i]);
            return vectors;
        }

        using var gate = new SemaphoreSlim(AzureConcurrency);
        await Task.WhenAll(texts.Select(async (text, i) =>
        {
            await gate.WaitAsync(ct);
            try { vectors[i] = await azure.EmbedAsync(text, ct); }
            finally { gate.Release(); }
        }));
        return vectors;
    }
}
