using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Infrastructure.NewDashboard;
using Reguliq.Api.Services.LocalDocs;
using Reguliq.Api.Services.NewDashboard;

namespace Reguliq.Api.Controllers.NewDashboard;

/// <summary>
/// Rulebook categories: each workspace's own category tree, up to three levels (e.g. Banking › Capital Adequacy ›
/// Regulations Re Capital Adequacy). Everyone in the workspace can read it; only admins (super_admin: the platform
/// admin and each workspace's Admin) change it. A workspace with no categories can import the CBUAE Rulebook
/// structure as a starting point.
/// </summary>
[ApiController]
[Route("nd/rulebook-categories")]
public class RulebookCategoriesController(
    AppDbContext db,
    SupabaseJwtValidator jwt,
    IWebHostEnvironment env,
    IHttpClientFactory httpClientFactory,
    NdRegulationUploadService uploadService,
    NdDashboardCacheService dashboardCache,
    IServiceScopeFactory scopeFactory,
    ILogger<RulebookCategoriesController> logger) : NdControllerBase
{
    /// <summary>Authority › category › sub-category › rulebook (e.g. Central Bank of the UAE › Banking › Capital
    /// Adequacy › Regulations Re Capital Adequacy).</summary>
    public const int MaxDepth = 4;
    private const string SampleFile = "rulebook-categories-cbuae.json";

    public record CategoryRequest(string? Name, string? Description, Guid? ParentId);

    /// <summary>Largest rulebook document the import downloads.</summary>
    private const long MaxDocumentBytes = 60L * 1024 * 1024;

    private sealed record SampleNode(string Name, List<SampleNode>? Children, string? SourceUrl, string? DocumentUrl);

    private sealed record SamplePack(List<SampleNode> Categories);

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "maker", "checker", "reviewer");
        if (error != null) return error;

        await FillMissingSampleLinksAsync(ct);
        var rows = await db.NdRulebookCategories.AsNoTracking()
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .ToListAsync(ct);
        var docCounts = await LinkedDocumentCountsAsync(ct);
        return Ok(new { success = true, data = rows.Select(c => ToDto(c, docCounts.GetValueOrDefault(c.Id))) });
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CategoryRequest body, CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, "super_admin");
        if (error != null) return error;

        var name = body.Name?.Trim() ?? "";
        if (name.Length == 0) return BadRequest(new { success = false, message = "Name is required." });

        if (body.ParentId is Guid parentId)
        {
            var parent = await db.NdRulebookCategories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == parentId, ct);
            if (parent == null) return NotFound(new { success = false, message = "Parent category not found." });
            if (await DepthOfAsync(parent, ct) >= MaxDepth)
                return BadRequest(new { success = false, message = $"Categories go at most {MaxDepth} levels deep." });
        }

        if (await SiblingNameTakenAsync(body.ParentId, name, null, ct))
            return Conflict(new { success = false, message = "A category with this name already exists here." });

        var siblings = db.NdRulebookCategories.Where(c => c.ParentId == body.ParentId);
        var row = new NdRulebookCategory
        {
            ParentId = body.ParentId,
            Name = name,
            Description = Clean(body.Description),
            SortOrder = (await siblings.MaxAsync(c => (int?)c.SortOrder, ct) ?? -1) + 1,
            CreatedBy = profile!.Id,
        };
        db.NdRulebookCategories.Add(row);
        await db.SaveChangesAsync(ct);
        return Ok(new { success = true, data = ToDto(row, 0) });
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] CategoryRequest body, CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(db, jwt, ct, "super_admin");
        if (error != null) return error;

        var row = await db.NdRulebookCategories.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (row == null) return NotFound(new { success = false, message = "Category not found." });

        var name = body.Name?.Trim() ?? "";
        if (name.Length == 0) return BadRequest(new { success = false, message = "Name is required." });
        if (await SiblingNameTakenAsync(row.ParentId, name, row.Id, ct))
            return Conflict(new { success = false, message = "A category with this name already exists here." });

        row.Name = name;
        row.Description = Clean(body.Description);
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Ok(new { success = true, data = ToDto(row, 0) });
    }

    /// <summary>Deletes the category and everything under it.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(db, jwt, ct, "super_admin");
        if (error != null) return error;

        var all = await db.NdRulebookCategories.ToListAsync(ct);
        var root = all.FirstOrDefault(c => c.Id == id);
        if (root == null) return NotFound(new { success = false, message = "Category not found." });

        var toDelete = new List<NdRulebookCategory> { root };
        for (var i = 0; i < toDelete.Count; i++)
            toDelete.AddRange(all.Where(c => c.ParentId == toDelete[i].Id));

        db.NdRulebookCategories.RemoveRange(toDelete);
        await db.SaveChangesAsync(ct);
        return Ok(new { success = true, deleted = toDelete.Count });
    }

    /// <summary>Imports the CBUAE Rulebook structure into an empty workspace. Refused once any category exists.</summary>
    [HttpPost("import-sample")]
    public async Task<IActionResult> ImportSample(CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, "super_admin");
        if (error != null) return error;

        if (await db.NdRulebookCategories.AnyAsync(ct))
            return Conflict(new { success = false, message = "This workspace already has rulebook categories." });

        var path = Path.Combine(env.ContentRootPath, "SeedData", SampleFile);
        if (!System.IO.File.Exists(path))
            return StatusCode(500, new { success = false, message = "Sample rulebook categories are not available." });

        var pack = JsonSerializer.Deserialize<SamplePack>(
            await System.IO.File.ReadAllTextAsync(path, ct),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        var added = 0;
        void AddLevel(IEnumerable<SampleNode>? nodes, Guid? parentId, int depth)
        {
            if (nodes == null || depth > MaxDepth) return;
            var order = 0;
            foreach (var node in nodes)
            {
                var name = node.Name?.Trim();
                if (string.IsNullOrEmpty(name)) continue;
                var row = new NdRulebookCategory
                {
                    ParentId = parentId,
                    Name = name,
                    SortOrder = order++,
                    SourceUrl = Clean(node.SourceUrl),
                    DocumentUrl = Clean(node.DocumentUrl),
                    CreatedBy = profile!.Id,
                };
                db.NdRulebookCategories.Add(row);
                added++;
                AddLevel(node.Children, row.Id, depth + 1);
            }
        }

        AddLevel(pack?.Categories, null, 1);
        await db.SaveChangesAsync(ct);
        return Ok(new { success = true, imported = added });
    }

    /// <summary>
    /// Downloads one rulebook's official document from its source link, adds it to Regulation Documents filed under
    /// this category, and starts parse and extract on the server. Skipped when the category already has a document.
    /// The page calls this once per rulebook, so a large import shows progress and can be stopped.
    /// </summary>
    [HttpPost("{id:guid}/import-document")]
    public async Task<IActionResult> ImportDocument(Guid id, CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, "super_admin");
        if (error != null) return error;

        var category = await db.NdRulebookCategories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (category == null) return NotFound(new { success = false, message = "Category not found." });
        if (string.IsNullOrWhiteSpace(category.DocumentUrl)
            || !Uri.TryCreate(category.DocumentUrl, UriKind.Absolute, out var url)
            || url.Scheme != Uri.UriSchemeHttps)
            return BadRequest(new { success = false, message = "This category has no document to download." });

        if ((await LinkedDocumentCountsAsync(ct)).GetValueOrDefault(id) > 0)
            return Ok(new { success = true, skipped = true, message = "Already imported." });

        byte[] bytes;
        try
        {
            var client = httpClientFactory.CreateClient(nameof(RulebookCategoriesController));
            client.Timeout = TimeSpan.FromMinutes(3);
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                return StatusCode(502, new
                {
                    success = false,
                    blocked = response.StatusCode == System.Net.HttpStatusCode.Forbidden,
                    message = response.StatusCode == System.Net.HttpStatusCode.Forbidden
                        ? "The source site does not allow automatic downloads. Open the source and upload the document instead."
                        : $"The source returned {(int)response.StatusCode}.",
                });
            if (response.Content.Headers.ContentLength > MaxDocumentBytes)
                return StatusCode(502, new { success = false, message = "The document is too large to import." });
            bytes = await response.Content.ReadAsByteArrayAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Rulebook document download failed for {Url}", url);
            return StatusCode(502, new { success = false, message = "Could not download the document from the source." });
        }

        if (!IsPdf(bytes))
            return StatusCode(502, new { success = false, message = "The source did not return a PDF document." });

        return Ok(new { success = true, data = await AddRegulationAsync(category, bytes, SafeFileName(category.Name) + ".pdf", "application/pdf", profile!.Id, ct) });
    }

    /// <summary>
    /// Adds a regulation document the admin downloaded themselves (when the source site blocks automatic downloads)
    /// to Regulation Documents filed under this category, and starts parse and extract on the server.
    /// </summary>
    [HttpPost("{id:guid}/upload-document")]
    [RequestSizeLimit(MaxDocumentBytes)]
    public async Task<IActionResult> UploadDocument(Guid id, IFormFile? file, CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, "super_admin");
        if (error != null) return error;

        var category = await db.NdRulebookCategories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (category == null) return NotFound(new { success = false, message = "Category not found." });
        if (file == null || file.Length == 0) return BadRequest(new { success = false, message = "Choose a file to upload." });
        if (file.Length > MaxDocumentBytes) return BadRequest(new { success = false, message = "The file is too large." });
        if (await NdUploadLimits.CheckAsync(db, file.FileName, file.Length, ct) is { } limitError)
            return BadRequest(new { success = false, message = limitError });

        var fileName = Path.GetFileName(file.FileName);
        if (!SupportedDocumentTypes.IsSupported(fileName))
            return BadRequest(new { success = false, message = $"Allowed: {SupportedDocumentTypes.DescribeAllowed()}." });

        await using var stream = file.OpenReadStream();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        return Ok(new
        {
            success = true,
            data = await AddRegulationAsync(category, buffer.ToArray(), fileName, file.ContentType ?? "application/octet-stream", profile!.Id, ct),
        });
    }

    private static bool IsPdf(byte[] bytes) =>
        bytes.Length >= 5 && bytes.Length <= MaxDocumentBytes && bytes.AsSpan(0, 5).SequenceEqual("%PDF-"u8);

    /// <summary>Stores the file as a regulation document, files it under the category, and starts the same
    /// server-side parse and extract as a regulation uploaded on New analysis.</summary>
    private async Task<object> AddRegulationAsync(
        NdRulebookCategory category,
        byte[] bytes,
        string fileName,
        string contentType,
        Guid userId,
        CancellationToken ct)
    {
        var regDoc = await uploadService.UploadAndExtractAsync(bytes, fileName, contentType, null, userId, ct);
        db.NdRegulationDocumentCategories.Add(new NdRegulationDocumentCategory
        {
            RegulationDocumentId = regDoc.Id,
            CategoryId = category.Id,
        });
        await db.SaveChangesAsync(ct);
        dashboardCache.Invalidate();

        if (regDoc.StoredDocumentId is Guid storedId)
            NdGapEvidencePrepareService.StartInBackground(
                scopeFactory, [storedId], AnalysisWorkflowEngine.RegulPipelineHybrid, logger);

        return new { regulationDocumentId = regDoc.Id, name = regDoc.Name };
    }

    /// <summary>Visible (not deleted) regulation documents per category.</summary>
    private async Task<Dictionary<Guid, int>> LinkedDocumentCountsAsync(CancellationToken ct) =>
        await db.NdRegulationDocumentCategories.AsNoTracking()
            .Where(l => db.NdRegulationDocuments.Any(d => d.Id == l.RegulationDocumentId && d.Status != -1))
            .GroupBy(l => l.CategoryId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(ch => invalid.Contains(ch) ? ' ' : ch).ToArray()).Trim();
        if (cleaned.Length > 120) cleaned = cleaned[..120].Trim();
        return cleaned.Length == 0 ? "Rulebook document" : cleaned;
    }

    /// <summary>
    /// Categories imported before the sample pack carried links (or before categories and sub-categories had
    /// them) get their source and document links from the sample, matched by the full name path. Only empty
    /// links are filled; anything an admin renamed simply stops matching.
    /// </summary>
    private async Task FillMissingSampleLinksAsync(CancellationToken ct)
    {
        var missing = await db.NdRulebookCategories.AnyAsync(c => c.SourceUrl == null, ct);
        if (!missing) return;
        var pack = await LoadSampleAsync(ct);
        if (pack == null) return;

        var links = new Dictionary<string, SampleNode>(StringComparer.OrdinalIgnoreCase);
        void Index(IEnumerable<SampleNode>? nodes, string prefix)
        {
            foreach (var node in nodes ?? [])
            {
                var path = prefix + "" + node.Name.Trim();
                links[path] = node;
                Index(node.Children, path);
            }
        }
        Index(pack.Categories, "");

        var all = await db.NdRulebookCategories.ToListAsync(ct);
        var byId = all.ToDictionary(c => c.Id);
        string PathOf(NdRulebookCategory c) =>
            (c.ParentId is Guid pid && byId.TryGetValue(pid, out var parent) ? PathOf(parent) : "") + "" + c.Name.Trim();

        var changed = false;
        foreach (var c in all.Where(c => c.SourceUrl == null))
        {
            if (!links.TryGetValue(PathOf(c), out var node) || string.IsNullOrWhiteSpace(node.SourceUrl)) continue;
            c.SourceUrl = node.SourceUrl;
            c.DocumentUrl ??= Clean(node.DocumentUrl);
            changed = true;
        }
        if (changed) await db.SaveChangesAsync(ct);
    }

    private async Task<SamplePack?> LoadSampleAsync(CancellationToken ct)
    {
        var path = Path.Combine(env.ContentRootPath, "SeedData", SampleFile);
        if (!System.IO.File.Exists(path)) return null;
        return JsonSerializer.Deserialize<SamplePack>(
            await System.IO.File.ReadAllTextAsync(path, ct),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    private async Task<int> DepthOfAsync(NdRulebookCategory category, CancellationToken ct)
    {
        var depth = 1;
        var parentId = category.ParentId;
        while (parentId is Guid pid && depth <= MaxDepth)
        {
            parentId = await db.NdRulebookCategories.AsNoTracking()
                .Where(c => c.Id == pid).Select(c => c.ParentId).FirstOrDefaultAsync(ct);
            depth++;
        }
        return depth;
    }

    private Task<bool> SiblingNameTakenAsync(Guid? parentId, string name, Guid? exceptId, CancellationToken ct)
    {
        var lower = name.ToLower();
        return db.NdRulebookCategories.AnyAsync(
            c => c.ParentId == parentId && c.Name.ToLower() == lower && c.Id != exceptId, ct);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static object ToDto(NdRulebookCategory c, int documentCount) => new
    {
        id = c.Id,
        parentId = c.ParentId,
        name = c.Name,
        description = c.Description,
        sortOrder = c.SortOrder,
        sourceUrl = c.SourceUrl,
        hasDocumentSource = !string.IsNullOrWhiteSpace(c.DocumentUrl),
        documentUrl = c.DocumentUrl,
        documentCount,
        createdAt = c.CreatedAt,
    };
}
