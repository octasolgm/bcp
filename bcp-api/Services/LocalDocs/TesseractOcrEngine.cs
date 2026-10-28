using System.Collections.Concurrent;
using Tesseract;

namespace Reguliq.Api.Services.LocalDocs;

/// <summary>
/// Local, offline OCR via Tesseract — no API key, no per-page charge, nothing leaves this server.
/// Requires trained-language data on disk (see <see cref="_tessDataPath"/>); download once from
/// https://github.com/tesseract-ocr/tessdata_best (eng.traineddata is enough for English documents).
///
/// A single TesseractEngine instance cannot process two pages at once, and OCR (not rendering, not
/// PdfPig) is the dominant cost on a multi-page scanned document — so this holds a small pool of
/// engines instead of one, and lets callers OCR several pages concurrently. Each engine still loads
/// its trained-data model once and is reused for the life of the process, never rebuilt per page.
/// </summary>
public sealed class TesseractOcrEngine(IConfiguration config, ILogger<TesseractOcrEngine> logger) : IOcrEngine, IDisposable
{
    private readonly string _tessDataPath =
        config["LocalOcr:TessDataPath"] ?? Path.Combine(AppContext.BaseDirectory, "tessdata");
    private readonly string _language = config["LocalOcr:Language"] ?? "eng";

    /// <summary>
    /// How many pages this process OCRs at once. Each engine is CPU-bound and holds its own copy of
    /// the trained-data model in memory, so this is capped rather than set to the full core count.
    /// Override via LocalOcr:MaxConcurrency in appsettings if a deployment's CPU/RAM budget calls for it.
    /// </summary>
    public int MaxConcurrency { get; } =
        config.GetValue<int?>("LocalOcr:MaxConcurrency") ?? Math.Clamp(Environment.ProcessorCount, 1, 8);

    private readonly ConcurrentBag<TesseractEngine> _pool = [];
    private SemaphoreSlim? _gate;
    private bool _engineInitFailed;

    public bool IsAvailable => Directory.Exists(_tessDataPath)
        && File.Exists(Path.Combine(_tessDataPath, $"{_language}.traineddata"));

    /// <summary>OCR a single page image (PNG/JPEG bytes) to plain text. Returns "" on failure — never throws.
    /// Safe to call concurrently from multiple pages at once (see class remarks).</summary>
    public async Task<string> OcrImageAsync(byte[] imageBytes, CancellationToken ct = default)
    {
        if (imageBytes.Length == 0) return "";
        if (!IsAvailable)
        {
            logger.LogWarning(
                "Tesseract trained data not found at {Path} — OCR skipped, install {Lang}.traineddata to enable scanned-page support",
                _tessDataPath, _language);
            return "";
        }

        var gate = _gate ??= new SemaphoreSlim(MaxConcurrency, MaxConcurrency);
        await gate.WaitAsync(ct);
        TesseractEngine? engine = null;
        try
        {
            engine = RentOrCreateEngine();
            if (engine == null) return "";

            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var img = Pix.LoadFromMemory(imageBytes);
            using var page = engine.Process(img);
            var text = (page.GetText() ?? "").Trim();
            logger.LogInformation("Tesseract OCR took {Ms}ms for one page ({Chars} chars)", sw.ElapsedMilliseconds, text.Length);
            return text;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Tesseract OCR failed for one image — page falls back to whatever native text was found");
            return "";
        }
        finally
        {
            if (engine != null) _pool.Add(engine);
            gate.Release();
        }
    }

    /// <summary>Reuses a pooled engine if one is free; otherwise builds one more (up to <see cref="MaxConcurrency"/>,
    /// enforced by the semaphore in <see cref="OcrImageAsync"/>). Loading the trained-data model from disk is the
    /// expensive part, especially the "best" (large, high-accuracy) variant — so each engine built here lives for
    /// the rest of the process, not just this call.</summary>
    private TesseractEngine? RentOrCreateEngine()
    {
        if (_pool.TryTake(out var pooled)) return pooled;
        if (_engineInitFailed) return null;

        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            // LstmOnly, not Default — Default tries the legacy engine too if legacy data is present,
            // which only adds runtime; the "best"/"fast" trained-data variants are LSTM-only anyway.
            var engine = new TesseractEngine(_tessDataPath, _language, EngineMode.LstmOnly);
            logger.LogInformation("Tesseract engine initialized in {Ms}ms ({Lang})", sw.ElapsedMilliseconds, _language);
            return engine;
        }
        catch (Exception ex)
        {
            _engineInitFailed = true;
            logger.LogWarning(ex, "Could not initialize Tesseract engine — OCR disabled for this process");
            return null;
        }
    }

    public void Dispose()
    {
        foreach (var engine in _pool) engine.Dispose();
    }
}
