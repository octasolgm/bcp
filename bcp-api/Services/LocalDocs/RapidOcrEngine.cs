using System.Collections.Concurrent;
using RapidOcrNet;
using SkiaSharp;

namespace Reguliq.Api.Services.LocalDocs;

/// <summary>
/// Local, offline OCR via RapidOCR (PaddleOCR models exported to ONNX, run through
/// Microsoft.ML.OnnxRuntime) — no API key, no per-page charge, nothing leaves this server.
///
/// Unlike the Tesseract NuGet package (Windows-only native binaries), RapidOcrNet depends on
/// Microsoft.ML.OnnxRuntime and SkiaSharp.NativeAssets.Linux, both of which ship real Linux native
/// assets — this engine is expected to work on Azure App Service Linux with no extra setup (no
/// apt-get, no Docker), unlike Tesseract in its current form. Its models are bundled in the NuGet
/// package and copied next to the build output automatically — no separate download step.
///
/// ONNX Runtime inference sessions are documented as safe for concurrent use, but the RapidOcrNet
/// wrapper's own thread-safety isn't documented either way — so, like <see cref="TesseractOcrEngine"/>,
/// this holds a small pool of engine instances rather than assuming one instance is safe to share
/// across concurrent OCR calls.
/// </summary>
public sealed class RapidOcrEngine(ILogger<RapidOcrEngine> logger) : IOcrEngine, IDisposable
{
    public bool IsAvailable => !_initFailed;

    /// <summary>Same reasoning as TesseractOcrEngine — bounded by CPU/memory, not the full core count.</summary>
    public int MaxConcurrency { get; } = Math.Clamp(Environment.ProcessorCount, 1, 8);

    private readonly ConcurrentBag<RapidOcr> _pool = [];
    private readonly SemaphoreSlim _gate = new(Math.Clamp(Environment.ProcessorCount, 1, 8), Math.Clamp(Environment.ProcessorCount, 1, 8));
    private bool _initFailed;

    public async Task<string> OcrImageAsync(byte[] imageBytes, CancellationToken ct = default)
    {
        if (imageBytes.Length == 0) return "";
        if (_initFailed) return "";

        await _gate.WaitAsync(ct);
        RapidOcr? engine = null;
        try
        {
            engine = RentOrCreateEngine();
            if (engine == null) return "";

            return await Task.Run(() =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                using var bitmap = SKBitmap.Decode(imageBytes);
                if (bitmap == null) return "";
                var result = engine.Detect(bitmap, RapidOcrOptions.Default);
                var text = (result?.StrRes ?? "").Trim();
                logger.LogInformation("RapidOCR took {Ms}ms for one page ({Chars} chars)", sw.ElapsedMilliseconds, text.Length);
                return text;
            }, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "RapidOCR failed for one image — page falls back to whatever native text was found");
            return "";
        }
        finally
        {
            if (engine != null) _pool.Add(engine);
            _gate.Release();
        }
    }

    /// <summary>Reuses a pooled engine if one is free; otherwise builds one more (up to
    /// <see cref="MaxConcurrency"/>, enforced by the semaphore above). Loading the ONNX models is the
    /// expensive part, so each engine built here lives for the rest of the process, not just this call.</summary>
    private RapidOcr? RentOrCreateEngine()
    {
        if (_pool.TryTake(out var pooled)) return pooled;
        if (_initFailed) return null;

        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var engine = new RapidOcr();
            // InitModels()'s parameterless overload resolves its default "models/v5/..." path against the
            // process's current working directory, not the binary's actual location — under `dotnet run`
            // those differ (cwd is the project folder), so the bundled models silently "don't exist" even
            // though they were copied to the build output correctly. Passing absolute paths sidesteps that.
            var modelsDir = Path.Combine(AppContext.BaseDirectory, "models", "v5");
            // numThread: 1, deliberately — by default each ONNX session tries to use many CPU threads for
            // its own inference. We already run up to MaxConcurrency of these engines at once (our own
            // pool-level parallelism); if each one ALSO tried to multithread internally, that's severe
            // oversubscription (N engines x M threads each, all fighting over the same cores) — which reads
            // as "stuck for minutes" rather than a clean speedup. One thread per engine, parallelism comes
            // from running several engines at once instead.
            using var sessionOptions = RapidOcr.GetDefaultSessionOptions(numThread: 1);
            engine.InitModels(
                Path.Combine(modelsDir, "ch_PP-OCRv5_mobile_det.onnx"),
                Path.Combine(modelsDir, "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx"),
                Path.Combine(modelsDir, "latin_PP-OCRv5_rec_mobile_infer.onnx"),
                Path.Combine(modelsDir, "ppocrv5_latin_dict.txt"),
                sessionOptions);
            logger.LogInformation("RapidOCR engine initialized in {Ms}ms", sw.ElapsedMilliseconds);
            return engine;
        }
        catch (Exception ex)
        {
            _initFailed = true;
            logger.LogWarning(ex, "Could not initialize RapidOCR engine — OCR disabled for this process");
            return null;
        }
    }

    public void Dispose()
    {
        foreach (var engine in _pool) engine.Dispose();
        _gate.Dispose();
    }
}
