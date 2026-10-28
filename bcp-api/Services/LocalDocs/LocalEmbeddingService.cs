using SmartComponents.LocalEmbeddings;

namespace Reguliq.Api.Services.LocalDocs;

/// <summary>
/// Local, offline text embeddings via SmartComponents.LocalEmbeddings (bge-micro-v2, quantized ONNX,
/// run through Microsoft.ML.OnnxRuntime — the same runtime already used by <see cref="RapidOcrEngine"/>,
/// just for a different model). No API key, no per-clause charge, nothing leaves this server — same
/// reasoning as the local OCR engines. 384-dim output, matching the `vector(384)` column on
/// <see cref="Data.Entities.NdLocalDocumentExtractionSection"/>.
///
/// The model file is fetched from Hugging Face at build time by the SmartComponents.LocalEmbeddings
/// package itself (cached under the NuGet package's own cache dir after the first restore/build) — not
/// something this service downloads or manages.
///
/// Registered as a singleton (see Program.cs) — loading the ONNX model is the expensive part, so one
/// instance lives for the life of the process, same pattern as RapidOcrEngine's pooled engines.
/// </summary>
public sealed class LocalEmbeddingService : IDisposable
{
    private readonly LocalEmbedder _embedder = new();

    /// <summary>Embeds one piece of text (a clause/section's text) into a 384-dim vector.</summary>
    public float[] Embed(string text)
    {
        var embedding = _embedder.Embed(text);
        return embedding.Values.ToArray();
    }

    public void Dispose() => _embedder.Dispose();
}
