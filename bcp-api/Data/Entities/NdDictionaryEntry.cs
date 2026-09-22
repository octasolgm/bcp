namespace Reguliq.Api.Data.Entities;

/// <summary>
/// One acronym/full-form pair for query expansion (hybrid pipeline Step 1 — see
/// docs/pipeline/HYBRID-ANALYSIS-PIPELINE-PLAN.md). Harvested automatically from every
/// document's parsed text when structural Extract runs (see <c>AcronymHarvester</c>) — a
/// document almost always spells a term out once as "Full Form (ABBR)", so this is a
/// text-shape regex, not an AI call, same philosophy as <c>LocalSectionSplitter</c>. A small
/// number of entries are seeded manually at startup for terms no processed document has
/// spelled out yet (see dictionary-seed.json).
/// </summary>
public class NdDictionaryEntry
{
    public Guid Id { get; set; }

    public string Acronym { get; set; } = "";
    public string Definition { get; set; } = "";

    /// <summary>"auto" (harvested from a document) or "manual" (from the seed file).</summary>
    public string Source { get; set; } = "auto";

    /// <summary>StoredDocumentId this pair was harvested from — null for manual seed entries.</summary>
    public Guid? SourceDocumentId { get; set; }
    public int? SourcePage { get; set; }

    /// <summary>Lets an admin soft-disable a bad auto-harvested pair (false-positive match)
    /// without losing the harvest provenance — never hard-deleted.</summary>
    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
