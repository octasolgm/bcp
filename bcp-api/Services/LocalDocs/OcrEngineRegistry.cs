namespace Reguliq.Api.Services.LocalDocs;

/// <summary>Every engine's route key — also the value stored in nd_local_document_extractions.engine.
/// Tesseract/RapidOcr are per-page IOcrEngine implementations (see OcrEngineRegistry.Resolve below).
/// DoclingLight/DoclingGlm are a different shape entirely — Docling converts the whole PDF itself via a
/// local Python service (see DoclingClient) — so they're valid route/engine values but are NOT resolved
/// through IOcrEngine; LocalDocumentExtractionService branches on them separately.</summary>
public static class OcrEngineNames
{
    public const string Tesseract = "tesseract";
    public const string RapidOcr = "rapidocr";
    public const string DoclingLight = "docling-light";
    public const string DoclingGlm = "docling-glm";

    public static readonly IReadOnlyList<string> All = [Tesseract, RapidOcr, DoclingLight, DoclingGlm];

    /// <summary>The two Docling variants don't implement IOcrEngine (whole-document, not per-page).</summary>
    public static bool IsDocling(string engine) =>
        string.Equals(engine, DoclingLight, StringComparison.OrdinalIgnoreCase)
        || string.Equals(engine, DoclingGlm, StringComparison.OrdinalIgnoreCase);

    public static bool IsValid(string engine) => All.Contains(engine, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Resolves the OCR engine a request asked for by name (the "{engine}" route segment) — lets the exact
/// same document be parsed by more than one engine and compared, instead of the app being locked to one.
/// </summary>
public sealed class OcrEngineRegistry(TesseractOcrEngine tesseract, RapidOcrEngine rapidOcr)
{
    public IOcrEngine Resolve(string engine) => engine.ToLowerInvariant() switch
    {
        OcrEngineNames.Tesseract => tesseract,
        OcrEngineNames.RapidOcr => rapidOcr,
        _ => throw new NotSupportedException(
            $"Unknown OCR engine '{engine}'. Supported: {string.Join(", ", OcrEngineNames.All)}."),
    };
}
