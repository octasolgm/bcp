using Reguliq.Api.Data.NewDashboard.Entities;
using Pgvector;

namespace Reguliq.Api.Data.Entities;

/// <summary>
/// A search passage: a ~150-300 word piece of one extracted section, with the heading path it sits under
/// ("AML Manual > Annex 1 > B.18 Other payment technologies"). Retrieval pipeline v4+ searches passages
/// instead of whole sections, because a section can run to thousands of words while the embedding model
/// reads only its first ~380, so evidence deep inside a long section was invisible to the meaning search.
/// Sections (<see cref="NdLocalDocumentExtractionSection"/>) stay the unit for references and finalize.
/// Built by <see cref="Services.LocalDocs.NdPassageIndexService"/>; re-indexing a document replaces its rows.
/// </summary>
public class NdLocalDocumentPassage : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid ExtractionId { get; set; }

    /// <summary>Section row this passage was cut from.</summary>
    public Guid SectionId { get; set; }

    public int SectionIndex { get; set; }
    public int PassageIndex { get; set; }

    /// <summary>The owning section's number ("7.5", "Annex 1"), used for citation labels like sections.</summary>
    public string? ClauseNo { get; set; }

    /// <summary>Document title and the headings above this passage, " > " separated.</summary>
    public string HeadingPath { get; set; } = "";

    public string PassageText { get; set; } = "";
    public int? SourcePage { get; set; }

    /// <summary>Vector from <see cref="EmbeddingModel"/>; the column has no fixed size so the model can change
    /// (vectors of different models are never compared: retrieval only uses rows of the configured model).</summary>
    public Vector? Embedding { get; set; }

    public string EmbeddingModel { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Owning workspace (nd_workspaces.id).</summary>
    public Guid? TenantId { get; set; }

    /// <summary>Text the search scores: the heading path gives a passage its context ("Possible indicators" under
    /// "B.18 Other payment technologies").</summary>
    public string SearchText => string.IsNullOrWhiteSpace(HeadingPath) ? PassageText : $"{HeadingPath}\n{PassageText}";
}
