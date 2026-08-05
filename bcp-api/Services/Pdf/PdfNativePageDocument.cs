using System.Text;
using Reguliq.Api.Services.LandingAi;
using UglyToad.PdfPig;

namespace Reguliq.Api.Services.Pdf;

/// <summary>
/// Per-page PDF text map (Regul.ai-style <c>[page N]</c> density) for accurate viewer page refs.
/// Landing AI markdown often has sparse/wrong markers; native extraction aligns with the PDF viewer.
/// </summary>
public sealed class PdfNativePageDocument
{
    public int TotalPages { get; }
    public string Markdown { get; }
    public PolicyPageResolver.PolicyPageResolveContext ResolveContext { get; }

    private PdfNativePageDocument(int totalPages, string markdown, PolicyPageResolver.PolicyPageResolveContext ctx)
    {
        TotalPages = totalPages;
        Markdown = markdown;
        ResolveContext = ctx;
    }

    public static PdfNativePageDocument? TryCreate(byte[] pdfBytes)
    {
        if (pdfBytes.Length < 16) return null;

        try
        {
            using var pdf = PdfDocument.Open(pdfBytes);
            var totalPages = pdf.NumberOfPages;
            if (totalPages <= 0) return null;

            var sb = new StringBuilder();
            foreach (var page in pdf.GetPages())
            {
                var pageNum = page.Number;
                var text = (page.Text ?? "").Trim();
                sb.AppendLine($"{PolicyPageResolver.PageMarkerPrefix}{pageNum} -->");
                if (text.Length > 0)
                    sb.AppendLine(text);
                sb.AppendLine();
            }

            var markdown = sb.ToString().Trim();
            return markdown.Length == 0 ? null : FromMarkdown(markdown, totalPages);
        }
        catch
        {
            return null;
        }
    }

    internal static PdfNativePageDocument FromMarkdown(string markdown, int totalPages)
    {
        var ctx = PolicyPageResolver.CreateResolveContext(markdown, totalPages);
        return new PdfNativePageDocument(totalPages, markdown, ctx);
    }

    /// <summary>Resolve 1-based PDF viewer page for a clause/section (no Landing AI hints).</summary>
    public int? ResolveSectionPage(string pointId, string? title, string text)
    {
        if (string.IsNullOrWhiteSpace(pointId) && string.IsNullOrWhiteSpace(text))
            return null;

        return PolicyPageResolver.ResolveGovPointPage(
            ResolveContext,
            pointId,
            pointId,
            title,
            text,
            aiPageHint: null);
    }
}
