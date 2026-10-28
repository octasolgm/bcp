using System.Net.Http.Headers;
using System.Text.Json;

namespace Reguliq.Api.Services.LocalDocs;

public sealed record DoclingConvertResult(string Markdown, int Pages, double ElapsedSeconds);

/// <summary>
/// Calls the local Docling test service (docling-service/server.py — a small Python/FastAPI wrapper,
/// not something this .NET app can run directly, since Docling is Python-only). Local testing only —
/// not a production integration. Two modes:
///
///   - "light": Docling's default pipeline (layout model + RapidOCR under the hood). Seconds per page.
///   - "glm":   Docling's VLM pipeline using GLM-OCR. Far more accurate in testing, but roughly 21
///     minutes PER PAGE on this CPU-only machine — only practical for short documents until this runs
///     on a GPU. See docs/pipeline/LIBRARY-REFERENCE.md.
///
/// Unlike Tesseract/RapidOCR (which OCR one page image at a time, fitting IOcrEngine), Docling converts
/// the WHOLE PDF itself in one call — it does its own page splitting/layout analysis internally. This
/// client does not implement IOcrEngine for that reason; it's a different shape of integration entirely.
/// </summary>
public sealed class DoclingClient(HttpClient http)
{
    public async Task<DoclingConvertResult> ConvertAsync(
        byte[] pdfBytes, string fileName, string mode, CancellationToken ct)
    {
        using var content = new MultipartFormDataContent();
        using var fileContent = new ByteArrayContent(pdfBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        content.Add(fileContent, "file", fileName);

        using var response = await http.PostAsync($"/convert?mode={mode}", content, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Docling service returned {(int)response.StatusCode}: {body}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var json = await JsonSerializer.DeserializeAsync<JsonElement>(stream, cancellationToken: ct);
        return new DoclingConvertResult(
            Markdown: json.GetProperty("markdown").GetString() ?? "",
            Pages: json.GetProperty("pages").GetInt32(),
            ElapsedSeconds: json.GetProperty("elapsedSeconds").GetDouble());
    }
}
