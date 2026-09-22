using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.Entities;
using Reguliq.Api.Infrastructure.NewDashboard;

namespace Reguliq.Api.Controllers.NewDashboard;

/// <summary>
/// Admin view/curation of the query-expansion acronym dictionary (hybrid pipeline Step 1 — see
/// docs/roadmap/QUERY-EXPANSION-PLAN.md). Rows are mostly auto-harvested by
/// <c>DictionaryExpansionService</c> right after structural Extract; this controller only lets an
/// admin see them, fix an "unresolved" placeholder (empty Definition), deactivate noise, or add a
/// manual entry.
/// </summary>
[ApiController]
[Route("nd/dictionary")]
public class NdDictionaryController(AppDbContext db, SupabaseJwtValidator jwt) : NdControllerBase
{
    public record DictionaryEntryRequest(string Acronym, string? Definition, bool? IsActive);

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var (_, _, error) = await RequireAuthWithUserAsync(db, jwt, ct,
            "super_admin", "maker", "checker", "reviewer");
        if (error != null) return error;

        var rows = await db.NdDictionaryEntries.AsNoTracking()
            .OrderBy(e => e.Acronym)
            .ToListAsync(ct);

        var docIds = rows.Where(r => r.SourceDocumentId.HasValue).Select(r => r.SourceDocumentId!.Value).Distinct().ToList();
        var docNames = docIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.StoredDocuments.AsNoTracking()
                .Where(d => docIds.Contains(d.Id))
                .ToDictionaryAsync(d => d.Id, d => d.OriginalFileName ?? d.Title ?? "document", ct);

        return Ok(new
        {
            success = true,
            data = rows.Select(e => new
            {
                id = e.Id,
                acronym = e.Acronym,
                definition = e.Definition,
                isUnresolved = string.IsNullOrEmpty(e.Definition),
                source = e.Source,
                sourceDocumentId = e.SourceDocumentId,
                sourceDocumentName = e.SourceDocumentId.HasValue
                    ? docNames.GetValueOrDefault(e.SourceDocumentId.Value)
                    : null,
                sourcePage = e.SourcePage,
                isActive = e.IsActive,
                createdAt = e.CreatedAt,
            }),
        });
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] DictionaryEntryRequest body, CancellationToken ct)
    {
        var (_, _, error) = await RequirePlatformAdminWithUserAsync(db, jwt, ct);
        if (error != null) return error;

        var acronym = body.Acronym.Trim();
        var definition = (body.Definition ?? "").Trim();
        if (acronym.Length == 0)
            return BadRequest(new { success = false, message = "Acronym is required." });

        var exists = await db.NdDictionaryEntries.AnyAsync(
            e => e.Acronym.ToLower() == acronym.ToLower() && e.Definition.ToLower() == definition.ToLower(), ct);
        if (exists)
            return BadRequest(new { success = false, message = "That acronym/definition pair already exists." });

        var row = new NdDictionaryEntry
        {
            Acronym = acronym,
            Definition = definition,
            Source = "manual",
            IsActive = true,
        };
        db.NdDictionaryEntries.Add(row);
        await db.SaveChangesAsync(ct);
        return Ok(new { success = true, data = row });
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] DictionaryEntryRequest body, CancellationToken ct)
    {
        var (_, _, error) = await RequirePlatformAdminWithUserAsync(db, jwt, ct);
        if (error != null) return error;

        var row = await db.NdDictionaryEntries.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (row == null) return NotFound(new { success = false, message = "Not found" });

        if (!string.IsNullOrWhiteSpace(body.Acronym)) row.Acronym = body.Acronym.Trim();
        if (body.Definition != null) row.Definition = body.Definition.Trim();
        if (body.IsActive.HasValue) row.IsActive = body.IsActive.Value;

        await db.SaveChangesAsync(ct);
        return Ok(new { success = true, data = row });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var (_, _, error) = await RequirePlatformAdminWithUserAsync(db, jwt, ct);
        if (error != null) return error;

        var row = await db.NdDictionaryEntries.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (row == null) return NotFound(new { success = false, message = "Not found" });

        db.NdDictionaryEntries.Remove(row);
        await db.SaveChangesAsync(ct);
        return Ok(new { success = true, deleted = true });
    }
}
