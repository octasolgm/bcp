using Microsoft.Extensions.Options;
using Reguliq.Api.Services.Llm;

namespace Reguliq.Api.Services.LocalDocs;

public sealed class RegulRetrievalOptions
{
    /// <summary>Default embedding model for search passages (pipeline v4+) when the admin has not chosen one in
    /// Admin > Analysis prompts: "local" (bge-micro-v2 on this server, free) or "azure-openai" (the
    /// AzureOpenAI:EmbeddingDeployment model, e.g. text-embedding-3-small). Passages embedded with another model are
    /// rebuilt in the background or before the next search; nothing needs re-parsing or re-extracting.</summary>
    public string EmbeddingProvider { get; set; } = "local";
}

/// <summary>
/// One place that turns passage and query text into vectors for retrieval pipeline v4+, so the index and the
/// queries always use the same model. <see cref="ModelNameAsync"/> is stored on every passage; vectors of different
/// models are never compared. The model is resolved once per instance (one analysis job, one indexing job), so an
/// admin switch in the middle of a run cannot mix models: the next job picks it up.
/// </summary>
public sealed class PassageEmbeddingService(
    LocalEmbeddingService local,
    AzureOpenAIEmbeddingClient azure,
    IOptions<AzureOpenAIOptions> azureOptions,
    RegulWorkflowLlmSettingsService settings,
    ILogger<PassageEmbeddingService> logger)
{
    public const string LocalModelName = "local:bge-micro-v2";
    private const int AzureBatchSize = 16;
    private const int AzureConcurrency = 4;

    private bool? _usesAzure;

    /// <summary>True when Azure OpenAI has an endpoint and key, so it can be chosen for search passages.</summary>
    public static bool IsAzureConfigured(AzureOpenAIOptions o) =>
        !string.IsNullOrWhiteSpace(o.Endpoint) && !string.IsNullOrWhiteSpace(o.ApiKey) && !string.IsNullOrWhiteSpace(o.EmbeddingDeployment);

    private async Task<bool> UsesAzureAsync(CancellationToken ct)
    {
        if (_usesAzure is { } known) return known;
        var wantsAzure = await settings.GetEmbeddingProviderAsync(ct) == RegulWorkflowLlmSettingsService.EmbeddingProviderAzureOpenAi;
        if (wantsAzure && !IsAzureConfigured(azureOptions.Value))
        {
            logger.LogWarning(
                "Search passages are set to Azure OpenAI embeddings but AzureOpenAI:Endpoint / ApiKey / EmbeddingDeployment is not configured; using the local model");
            wantsAzure = false;
        }

        _usesAzure = wantsAzure;
        return wantsAzure;
    }

    public async Task<string> ModelNameAsync(CancellationToken ct) =>
        await UsesAzureAsync(ct) ? $"azure-openai:{azureOptions.Value.EmbeddingDeployment}" : LocalModelName;

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct) =>
        await UsesAzureAsync(ct) ? (await azure.EmbedBatchAsync([text], ct))[0] : local.Embed(text);

    public async Task<float[][]> EmbedManyAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        var vectors = new float[texts.Count][];
        if (!await UsesAzureAsync(ct))
        {
            for (var i = 0; i < texts.Count; i++) vectors[i] = local.Embed(texts[i]);
            return vectors;
        }

        var batches = Enumerable.Range(0, (texts.Count + AzureBatchSize - 1) / AzureBatchSize).ToList();
        using var gate = new SemaphoreSlim(AzureConcurrency);
        await Task.WhenAll(batches.Select(async b =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var start = b * AzureBatchSize;
                var chunk = texts.Skip(start).Take(AzureBatchSize).ToList();
                var result = await azure.EmbedBatchAsync(chunk, ct);
                for (var i = 0; i < result.Length; i++) vectors[start + i] = result[i];
            }
            finally
            {
                gate.Release();
            }
        }));
        return vectors;
    }
}
