using System.Text.Json;

namespace Reguliq.Api.Services.LocalDocs;

public sealed class AzureDocumentIntelligenceOptions
{
    public string Endpoint { get; set; } = "";
    public string ApiKey { get; set; } = "";
}

public sealed record AzureDocIntelligenceResult(string Markdown, int Pages, double ElapsedSeconds);

/// <summary>
/// Calls Azure AI Document Intelligence's prebuilt-layout model (submit-then-poll REST API) to convert a
/// whole PDF/image/Office document to markdown in one cloud call — no local OCR model needed. Same shape
/// as <see cref="DoclingClient"/> (whole-document, not per-page), so it plugs into the local pipeline the
/// same way: a valid "{engine}" route value that LocalDocumentExtractionService branches on separately
/// from the per-page IOcrEngine engines (Tesseract/RapidOCR). See OcrEngineNames.IsAzureDocIntelligence.
/// </summary>
public sealed class AzureDocumentIntelligenceClient(HttpClient http, Microsoft.Extensions.Options.IOptions<AzureDocumentIntelligenceOptions> options)
{
    private const string ApiVersion = "2024-11-30";

    /// <summary>
    /// Analyzes a document Azure can fetch itself via <paramref name="sourceUrl"/> (a short-lived signed
    /// URL into our own storage) rather than posting the file bytes in the request body. The direct-body
    /// upload path is capped at 4 MB by Azure regardless of pricing tier; giving Azure a URL to fetch
    /// instead supports files up to 500 MB, which real scanned regulation/internal PDFs regularly exceed.
    /// </summary>
    public async Task<AzureDocIntelligenceResult> ConvertAsync(string sourceUrl, CancellationToken ct)
    {
        var opts = options.Value;
        if (string.IsNullOrWhiteSpace(opts.Endpoint) || string.IsNullOrWhiteSpace(opts.ApiKey))
            throw new InvalidOperationException(
                "Azure Document Intelligence is not configured (AzureDocumentIntelligence:Endpoint / ApiKey).");

        var started = DateTimeOffset.UtcNow;

        using var content = new StringContent(
            JsonSerializer.Serialize(new { urlSource = sourceUrl }),
            System.Text.Encoding.UTF8,
            "application/json");

        var analyzeUrl =
            $"documentintelligence/documentModels/prebuilt-layout:analyze" +
            $"?api-version={ApiVersion}&outputContentFormat=markdown";

        using var request = new HttpRequestMessage(HttpMethod.Post, analyzeUrl) { Content = content };
        request.Headers.Add("Ocp-Apim-Subscription-Key", opts.ApiKey);

        using var submitResponse = await http.SendAsync(request, ct);
        if (!submitResponse.IsSuccessStatusCode)
        {
            var body = await submitResponse.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Azure Document Intelligence returned {(int)submitResponse.StatusCode} on submit: {body}");
        }

        var operationLocation = submitResponse.Headers.TryGetValues("Operation-Location", out var values)
            ? values.FirstOrDefault()
            : null;
        if (string.IsNullOrWhiteSpace(operationLocation))
            throw new InvalidOperationException("Azure Document Intelligence did not return an Operation-Location header.");

        // Submit-then-poll: analysis runs async server-side, poll the operation URL until it's done.
        // A normal-sized document finishes in seconds; give it generous headroom for large scanned PDFs.
        var deadline = DateTimeOffset.UtcNow.AddMinutes(10);
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            using var pollRequest = new HttpRequestMessage(HttpMethod.Get, operationLocation);
            pollRequest.Headers.Add("Ocp-Apim-Subscription-Key", opts.ApiKey);
            using var pollResponse = await http.SendAsync(pollRequest, ct);
            var pollBody = await pollResponse.Content.ReadAsStringAsync(ct);

            if (!pollResponse.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Azure Document Intelligence returned {(int)pollResponse.StatusCode} while polling: {pollBody}");

            using var doc = JsonDocument.Parse(pollBody);
            var status = doc.RootElement.TryGetProperty("status", out var statusEl) ? statusEl.GetString() : null;

            if (string.Equals(status, "succeeded", StringComparison.OrdinalIgnoreCase))
            {
                var analyzeResult = doc.RootElement.GetProperty("analyzeResult");
                var markdown = analyzeResult.TryGetProperty("content", out var contentEl)
                    ? contentEl.GetString() ?? ""
                    : "";
                var pageCount = analyzeResult.TryGetProperty("pages", out var pagesEl) && pagesEl.ValueKind == JsonValueKind.Array
                    ? pagesEl.GetArrayLength()
                    : 1;

                return new AzureDocIntelligenceResult(
                    markdown, Math.Max(pageCount, 1), (DateTimeOffset.UtcNow - started).TotalSeconds);
            }

            if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
            {
                var error = doc.RootElement.TryGetProperty("error", out var errEl) ? errEl.ToString() : pollBody;
                throw new InvalidOperationException($"Azure Document Intelligence analysis failed: {error}");
            }

            if (DateTimeOffset.UtcNow > deadline)
                throw new TimeoutException("Azure Document Intelligence analysis did not finish within 10 minutes.");

            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }
}
