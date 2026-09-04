namespace Reguliq.Api.Services.LocalDocs;

/// <summary>
/// A local, offline OCR engine — reads text out of a rendered page image. Implementations
/// (<see cref="TesseractOcrEngine"/>, <see cref="RapidOcrEngine"/>) are picked per-request via
/// <see cref="OcrEngineRegistry"/>, so the same document can be parsed by more than one engine and
/// compared. Nothing here calls out to any external service — every implementation runs in-process.
/// </summary>
public interface IOcrEngine
{
    /// <summary>False if this engine's model/trained data isn't present — callers should skip OCR
    /// gracefully (fall back to whatever native text was found) rather than fail the whole parse.</summary>
    bool IsAvailable { get; }

    /// <summary>How many pages this engine will OCR at once — bounded by CPU/memory, not by how many
    /// cores exist, since each concurrent OCR call holds real memory for its own model/session.</summary>
    int MaxConcurrency { get; }

    /// <summary>OCR a single page image (PNG bytes) to plain text. Returns "" on failure — never throws.</summary>
    Task<string> OcrImageAsync(byte[] imageBytes, CancellationToken ct = default);
}
