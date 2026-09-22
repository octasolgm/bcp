namespace Reguliq.Api.Data.Entities;

/// <summary>
/// One plain-language synonym pair for query expansion (hybrid pipeline Step 1 — see
/// docs/pipeline/HYBRID-ANALYSIS-PIPELINE-PLAN.md). Distinct from <see cref="NdDictionaryEntry"/>:
/// that table pairs an acronym with its spelled-out full form (harvested automatically from the
/// "Full Form (ABBR)" text shape); this table pairs two plain terms/phrases that mean the same
/// thing without either being a short form of the other (e.g. "beneficial owner" /
/// "ultimate beneficial owner").
///
/// Unlike acronyms, there is no reliable text shape to detect a synonym pair from — nothing in a
/// document marks "these two phrases mean the same thing." <see cref="SynonymCandidateHarvester"/>
/// instead compares a document's own sections pairwise by embedding similarity and flags
/// high-similarity, differently-worded pairs as unreviewed candidates: inserted with
/// <see cref="Source"/> = "auto" and <see cref="IsActive"/> = false, so they never affect a live
/// analysis run until an admin reviews and activates them on the synonym admin page. A row an
/// admin adds directly, or from the seed file, is <see cref="Source"/> = "manual" and starts
/// active immediately — no review gate needed for something a person typed in on purpose.
/// </summary>
public class NdSynonymEntry
{
    public Guid Id { get; set; }

    public string TermA { get; set; } = "";
    public string TermB { get; set; } = "";

    /// <summary>"auto" (embedding-similarity candidate, unreviewed) or "manual" (admin-added or
    /// from the seed file, trusted immediately).</summary>
    public string Source { get; set; } = "manual";

    /// <summary>StoredDocumentId this candidate was harvested from — null for manual/seed entries.</summary>
    public Guid? SourceDocumentId { get; set; }
    public int? SourcePage { get; set; }

    /// <summary>For manual/seed rows: whether the pair is in active use. For auto-harvested
    /// candidates: whether an admin has reviewed and approved it — false means "not yet used in
    /// analysis," not "disabled."</summary>
    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
