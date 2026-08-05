using Reguliq.Api.Data.Entities;
using Reguliq.Api.Services.LandingAi;
using Reguliq.Api.Services.Storage;

namespace Reguliq.Api.Services.Pdf;

/// <summary>
/// Single page-ref pipeline for internal sections and regulation points.
/// Landing AI: section/point numbers and text only. Page numbers: PDF-native + grounded markdown.
/// </summary>
public sealed class NdDocumentPageReferenceResolver(
    PdfNativePageDocumentLoader pdfPages,
    SupabaseStorageService storage)
{
    public async Task<int?> ResolveSectionPageAsync(
        StoredDocument doc,
        string? landingMarkdown,
        string sectionRef,
        string? title,
        string sectionText,
        CancellationToken ct = default)
    {
        var native = await pdfPages.TryLoadForDocumentAsync(doc, ct);
        if (native is not null)
        {
            var fromNative = native.ResolveSectionPage(sectionRef, title, sectionText);
            if (fromNative is > 0)
                return fromNative;
        }

        var resolveMarkdown = await LoadResolveMarkdownAsync(doc, landingMarkdown, ct);
        if (string.IsNullOrWhiteSpace(resolveMarkdown))
            return null;

        var maxPages = native?.TotalPages ?? PolicyPageResolver.EstimatePageCount(resolveMarkdown);
        var resolved = PolicyPageResolver.ResolveGovPointPage(
            resolveMarkdown,
            sectionRef,
            sectionRef,
            title,
            sectionText,
            aiPageHint: null,
            maxPages);
        return PolicyPageResolver.RefinePageGuess(resolved, sectionRef, maxPages);
    }

    private async Task<string?> LoadResolveMarkdownAsync(
        StoredDocument doc,
        string? landingMarkdown,
        CancellationToken ct)
    {
        if (!storage.IsConfigured || string.IsNullOrWhiteSpace(doc.StoragePath))
            return landingMarkdown;

        try
        {
            var bytes = await storage.DownloadAsync(doc.StoragePath, ct);
            var fileName = doc.OriginalFileName ?? Path.GetFileName(doc.StoragePath) ?? doc.Title ?? "document.pdf";
            if (!LandingAiDocumentFormats.IsPdf(fileName, bytes))
                return landingMarkdown;

            return PdfGroundedMarkdownBuilder.TryBuildResolveMarkdown(landingMarkdown, bytes)
                ?? landingMarkdown;
        }
        catch
        {
            return landingMarkdown;
        }
    }
}
