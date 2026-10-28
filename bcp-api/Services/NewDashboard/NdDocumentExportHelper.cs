using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Reguliq.Api.Services.LandingAi;
using Reguliq.Api.Services.Pdf;

namespace Reguliq.Api.Services.NewDashboard;

public static class NdDocumentExportHelper
{
    public static string SafeExportBaseName(string? name, string fallback = "document")
    {
        var raw = (name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(raw)) raw = fallback;
        raw = Path.GetFileNameWithoutExtension(raw);
        raw = Regex.Replace(raw, @"[^\w\-.]+", "_");
        raw = Regex.Replace(raw, @"_+", "_").Trim('_', '.');
        return string.IsNullOrWhiteSpace(raw) ? fallback : raw[..Math.Min(raw.Length, 80)];
    }

    public static string WrapInternalMarkdownForV4(string fileLabel, string markdown) =>
        $"=== DOCUMENT: {fileLabel.Trim()} ===\n{markdown.Trim()}";

    /// <summary>Same markdown shaping as Regul V4 <c>LoadInternalDocPayloadsAsync</c> (PDF page markers).</summary>
    public static string ResolveInternalMarkdownForV4(
        string? landingMarkdown,
        byte[]? fileBytes,
        string? fileName)
    {
        if (string.IsNullOrWhiteSpace(landingMarkdown))
            return "";

        var markdown = landingMarkdown.Trim();
        if (fileBytes is { Length: > 16 }
            && !string.IsNullOrWhiteSpace(fileName)
            && LandingAiDocumentFormats.IsPdf(fileName, fileBytes))
        {
            markdown = PdfGroundedMarkdownBuilder.TryBuildResolveMarkdown(markdown, fileBytes) ?? markdown;
        }

        return markdown;
    }

    public static byte[] Utf8Bytes(string text) => Encoding.UTF8.GetBytes(text);

    public static string PrettyJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "{}";
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return raw.Trim();
        }
    }
}
