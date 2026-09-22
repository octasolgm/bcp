using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.Entities;
using Reguliq.Api.Infrastructure.NewDashboard;

namespace Reguliq.Api.Controllers.NewDashboard;

/// <summary>
/// Admin view/curation of the query-expansion synonym dictionary (hybrid pipeline Step 1 — see
/// docs/roadmap/QUERY-EXPANSION-PLAN.md). Rows come from three places: the seed file, an admin
/// adding one directly here (both start active immediately), or DictionaryExpansionService's
/// embedding-similarity harvester suggesting one from a document (starts inactive — "unreviewed,"
/// not "disabled" — until an admin approves it via the Update endpoint below).
/// </summary>
[ApiController]
[Route("nd/synonyms")]
public class NdSynonymController(AppDbContext db, SupabaseJwtValidator jwt) : NdControllerBase
{
    public record SynonymEntryRequest(string? TermA, string? TermB, bool? IsActive);

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var (_, _, error) = await RequireAuthWithUserAsync(db, jwt, ct,
            "super_admin", "maker", "checker", "reviewer");
        if (error != null) return error;

        var rows = await db.NdSynonymEntries.AsNoTracking()
            .OrderBy(e => e.TermA)
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
                termA = e.TermA,
                termB = e.TermB,
                source = e.Source,
                sourceDocumentId = e.SourceDocumentId,
                sourceDocumentName = e.SourceDocumentId.HasValue
                    ? docNames.GetValueOrDefault(e.SourceDocumentId.Value)
                    : null,
                sourcePage = e.SourcePage,
                isPendingReview = e.Source == "auto" && !e.IsActive,
                isActive = e.IsActive,
                createdAt = e.CreatedAt,
            }),
        });
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SynonymEntryRequest body, CancellationToken ct)
    {
        var (_, _, error) = await RequirePlatformAdminWithUserAsync(db, jwt, ct);
        if (error != null) return error;

        var termA = (body.TermA ?? "").Trim();
        var termB = (body.TermB ?? "").Trim();
        if (termA.Length == 0 || termB.Length == 0)
            return BadRequest(new { success = false, message = "Both terms are required." });

        var exists = await db.NdSynonymEntries.AnyAsync(
            e => e.TermA.ToLower() == termA.ToLower() && e.TermB.ToLower() == termB.ToLower(), ct);
        if (exists)
            return BadRequest(new { success = false, message = "That synonym pair already exists." });

        var row = new NdSynonymEntry
        {
            TermA = termA,
            TermB = termB,
            Source = "manual",
            IsActive = true,
        };
        db.NdSynonymEntries.Add(row);
        await db.SaveChangesAsync(ct);
        return Ok(new { success = true, data = row });
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] SynonymEntryRequest body, CancellationToken ct)
    {
        var (_, _, error) = await RequirePlatformAdminWithUserAsync(db, jwt, ct);
        if (error != null) return error;

        var row = await db.NdSynonymEntries.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (row == null) return NotFound(new { success = false, message = "Not found" });

        if (!string.IsNullOrWhiteSpace(body.TermA)) row.TermA = body.TermA.Trim();
        if (!string.IsNullOrWhiteSpace(body.TermB)) row.TermB = body.TermB.Trim();
        if (body.IsActive.HasValue) row.IsActive = body.IsActive.Value;

        await db.SaveChangesAsync(ct);
        return Ok(new { success = true, data = row });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var (_, _, error) = await RequirePlatformAdminWithUserAsync(db, jwt, ct);
        if (error != null) return error;

        var row = await db.NdSynonymEntries.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (row == null) return NotFound(new { success = false, message = "Not found" });

        db.NdSynonymEntries.Remove(row);
        await db.SaveChangesAsync(ct);
        return Ok(new { success = true, deleted = true });
    }
}
