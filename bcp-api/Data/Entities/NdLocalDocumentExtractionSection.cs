using Reguliq.Api.Data.NewDashboard.Entities;
using Pgvector;

namespace Reguliq.Api.Data.Entities;

/// <summary>
/// One row per <c>LocalSection</c> (from <see cref="NdLocalDocumentExtraction.SectionsJson"/>), with its
/// embedding vector — pgvector needs one real column per row to search via <c>ORDER BY embedding &lt;=&gt;
/// query</c>, which a single JSONB blob can't provide. Internal documents only (see
/// docs/pipeline/HYBRID-ANALYSIS-PIPELINE-PLAN.md Step 0 — only the internal side is indexed; gov clauses
/// are the query side, never stored here). Populated by the background indexing job
/// (<c>IndexingWorkerHosted</c>) right after Extract succeeds; re-extracting a document replaces all of
/// its rows here rather than appending.
/// </summary>
public class NdLocalDocumentExtractionSection : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid ExtractionId { get; set; }

    /// <summary>0-based position within the section list at indexing time — not a stable clause identity
    /// on its own (clause numbering, when present, is <see cref="ClauseNo"/>).</summary>
    public int SectionIndex { get; set; }

    public string? ClauseNo { get; set; }
    public string ClauseText { get; set; } = "";
    public int? SourcePage { get; set; }

    /// <summary>384-dim embedding from the local ONNX model (bge-micro-v2 via SmartComponents.LocalEmbeddings)
    /// — see LocalEmbeddingService. Null until the indexing job has processed this row.</summary>
    public Vector? Embedding { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Owning workspace (nd_workspaces.id).</summary>
    public Guid? TenantId { get; set; }
}
