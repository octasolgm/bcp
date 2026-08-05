using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Reguliq.Api.Data;
using Reguliq.Api.Data.Entities;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Services.LandingAi;
using Reguliq.Api.Services.Pdf;
using Reguliq.Api.Services.Storage;

namespace Reguliq.Api.Services.NewDashboard;

/// <summary>Resolve PDF viewer pages for internal document library sections from native PDF text (preferred) or parse cache.</summary>
public sealed class NdInternalDocumentSectionPageService(
    AppDbContext db,
    LandingAiCacheRepository cache,
    SupabaseStorageService storage,
    PdfNativePageDocumentLoader pdfPages,
    NdDocumentPageReferenceResolver pageResolver,
    IServiceScopeFactory scopeFactory,
    ILogger<NdInternalDocumentSectionPageService> logger)
{
    private static readonly ConcurrentDictionary<Guid, SectionPageRepairJob> RepairJobs = new();

    public sealed record RefreshResult(int SectionCount, int PagesRefreshed);

    public sealed record SectionPageRepairJob(
        string Status,
        string? Label,
        int? Percent,
        int? SectionCount,
        int? PagesRefreshed,
        string? Error);

    public SectionPageRepairJob? GetRepairJob(Guid storedDocumentId) =>
        RepairJobs.TryGetValue(storedDocumentId, out var job) ? job : null;

    public void ClearRepairJob(Guid storedDocumentId) => RepairJobs.TryRemove(storedDocumentId, out _);

    /// <summary>Queue background page repair — returns immediately for large manuals.</summary>
    public bool TryQueueRefreshSectionPages(Guid storedDocumentId, out string? errorMessage)
    {
        errorMessage = null;
        if (RepairJobs.TryGetValue(storedDocumentId, out var existing)
            && string.Equals(existing.Status, "processing", StringComparison.OrdinalIgnoreCase))
        {
            errorMessage = "Page repair is already running for this document.";
            return false;
        }

        RepairJobs[storedDocumentId] = new SectionPageRepairJob(
            "processing",
            "Starting page repair…",
            0,
            null,
            null,
            null);

        _ = Task.Run(() => RunQueuedRepairAsync(storedDocumentId));
        return true;
    }

    private async Task RunQueuedRepairAsync(Guid storedDocumentId)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var svc = scope.ServiceProvider.GetRequiredService<NdInternalDocumentSectionPageService>();
            var result = await svc.RefreshSectionPagesAsync(
                storedDocumentId,
                (done, total, label) =>
                {
                    var pct = total > 0 ? (int)Math.Round(done * 100.0 / total) : 0;
                    RepairJobs[storedDocumentId] = new SectionPageRepairJob(
                        "processing",
                        label,
                        pct,
                        total,
                        null,
                        null);
                },
                CancellationToken.None);

            RepairJobs[storedDocumentId] = new SectionPageRepairJob(
                "completed",
                "Page repair finished.",
                100,
                result.SectionCount,
                result.PagesRefreshed,
                null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Background section page repair failed for doc {DocId}", storedDocumentId);
            RepairJobs[storedDocumentId] = new SectionPageRepairJob(
                "failed",
                "Page repair failed.",
                null,
                null,
                null,
                ex.Message);
        }
    }

    /// <summary>Recompute <see cref="NdInternalDocumentSection.SourcePage"/> from PDF page text or parse cache — no Landing AI credits.</summary>
    public Task<RefreshResult> RefreshSectionPagesAsync(Guid storedDocumentId, CancellationToken ct = default)
        => RefreshSectionPagesAsync(storedDocumentId, reportProgress: null, ct);

    public async Task<RefreshResult> RefreshSectionPagesAsync(
        Guid storedDocumentId,
        Action<int, int, string>? reportProgress,
        CancellationToken ct = default)
    {
        var doc = await db.StoredDocuments.FirstOrDefaultAsync(
            d => d.Id == storedDocumentId && (d.DocKind == "document" || d.DocKind == "internal"),
            ct) ?? throw new InvalidOperationException("Internal document not found.");

        var sections = await db.NdInternalDocumentSections
            .Where(s => s.StoredDocumentId == storedDocumentId)
            .ToListAsync(ct);
        if (sections.Count == 0)
            return new RefreshResult(0, 0);

        reportProgress?.Invoke(0, sections.Count, "Loading PDF for page repair…");

        var landingMarkdown = await LoadLandingParseMarkdownAsync(doc, ct);
        var native = await pdfPages.TryLoadForDocumentAsync(doc, ct);
        if (native is null && string.IsNullOrWhiteSpace(landingMarkdown))
            throw new InvalidOperationException(
                "No PDF text and no cached document parse found. Upload a PDF or run parse first.");

        if (native is not null && doc.Pages != native.TotalPages)
        {
            doc.Pages = native.TotalPages;
            doc.UpdatedAt = DateTimeOffset.UtcNow;
        }

        if (native is not null)
        {
            logger.LogInformation(
                "Section page repair for doc {DocId}: PDF-native + grounded Landing ({Pages} pages)",
                storedDocumentId,
                native.TotalPages);
        }
        else if (!string.IsNullOrWhiteSpace(landingMarkdown))
        {
            logger.LogInformation(
                "Section page repair for doc {DocId}: grounded Landing markdown fallback",
                storedDocumentId);
        }

        var refreshed = 0;
        var total = sections.Count;

        for (var i = 0; i < sections.Count; i++)
        {
            var section = sections[i];
            var resolved = await pageResolver.ResolveSectionPageAsync(
                doc,
                landingMarkdown,
                section.SectionRef,
                null,
                section.SectionText,
                ct);

            if (resolved is int p and > 0)
            {
                if (section.SourcePage != p)
                    refreshed++;
                section.SourcePage = p;
            }

            if (i == 0 || (i + 1) % 25 == 0 || i + 1 == total)
            {
                reportProgress?.Invoke(
                    i + 1,
                    total,
                    $"Resolving PDF pages ({i + 1}/{total})…");
            }
        }

        reportProgress?.Invoke(total, total, "Saving updated page references…");

        if (refreshed > 0 || native is not null || !string.IsNullOrWhiteSpace(landingMarkdown))
        {
            doc.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        logger.LogInformation(
            "Refreshed internal section page refs for doc {DocId}: {Refreshed}/{Count} updated (source={Source}, totalPages={TotalPages})",
            storedDocumentId,
            refreshed,
            sections.Count,
            native is not null ? "pdf-grounded" : "landing-grounded",
            native?.TotalPages ?? PolicyPageResolver.EstimatePageCount(landingMarkdown));

        return new RefreshResult(sections.Count, refreshed);
    }

    internal static int? ResolveSectionPage(
        string markdown,
        string sectionRef,
        string sectionText,
        int? pageHint,
        int? totalPages = null)
    {
        totalPages ??= PolicyPageResolver.EstimatePageCount(markdown);
        return ResolveFromLandingMarkdown(markdown, sectionRef, sectionText, pageHint, totalPages);
    }

    internal static int? ResolveSectionPage(
        PdfNativePageDocument native,
        string sectionRef,
        string sectionText)
        => native.ResolveSectionPage(sectionRef, null, sectionText);

    private static int? ResolveFromLandingContext(
        PolicyPageResolver.PolicyPageResolveContext ctx,
        string sectionRef,
        string sectionText,
        int? totalPages)
    {
        var resolved = PolicyPageResolver.ResolveGovPointPage(
            ctx,
            sectionRef,
            sectionRef,
            null,
            sectionText,
            aiPageHint: null);
        return PolicyPageResolver.RefinePageGuess(resolved, sectionRef, totalPages);
    }

    private static int? ResolveFromLandingMarkdown(
        string markdown,
        string sectionRef,
        string sectionText,
        int? pageHint,
        int? totalPages)
    {
        var resolved = PolicyPageResolver.ResolveGovPointPage(
            markdown,
            sectionRef,
            sectionRef,
            null,
            sectionText,
            pageHint,
            totalPages);
        return PolicyPageResolver.RefinePageGuess(resolved, sectionRef, totalPages);
    }

    private async Task<string?> LoadLandingParseMarkdownAsync(StoredDocument doc, CancellationToken ct)
    {
        var cacheKey = await NdStoredDocumentExtractionCache.EnsureKeyAsync(db, doc, ct);
        var fileHash = (doc.FileHash ?? "").Trim();
        var cached = await cache.ResolveParseCacheAsync(cacheKey, fileHash, "", ct);
        return cached?.Markdown;
    }

    private async Task<(string? Markdown, int? TotalPages)> LoadLandingParseContextAsync(
        StoredDocument doc,
        CancellationToken ct)
    {
        var cacheKey = await NdStoredDocumentExtractionCache.EnsureKeyAsync(db, doc, ct);
        var fileHash = (doc.FileHash ?? "").Trim();
        var cached = await cache.ResolveParseCacheAsync(cacheKey, fileHash, "", ct);
        var markdown = cached?.Markdown;

        var markerPages = PolicyPageResolver.EstimatePageCount(markdown);
        if (markerPages is > 1)
            return (markdown, markerPages);

        var pdfPages = await TryGetPdfPageCountAsync(doc, ct);
        if (pdfPages is > 1)
            return (markdown, pdfPages);

        return (markdown, markerPages ?? pdfPages);
    }

    private async Task<int?> TryGetPdfPageCountAsync(StoredDocument doc, CancellationToken ct)
    {
        if (doc.Pages is > 1) return doc.Pages;

        if (!storage.IsConfigured || string.IsNullOrWhiteSpace(doc.StoragePath))
            return null;

        try
        {
            var bytes = await storage.DownloadAsync(doc.StoragePath, ct);
            var fileName = doc.OriginalFileName ?? Path.GetFileName(doc.StoragePath) ?? doc.Title ?? "policy.pdf";
            if (!LandingAiDocumentFormats.IsPdf(fileName, bytes))
                return null;

            var count = LandingAiDocumentParseService.GetPdfPageCount(bytes);
            if (count > 1)
            {
                doc.Pages = count;
                doc.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
            }

            return count;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not read PDF page count for internal doc {DocId}", doc.Id);
            return null;
        }
    }
}
