using Reguliq.Api.Data.NewDashboard.Entities;
namespace Reguliq.Api.Data.Entities;

/// <summary>
/// Persisted result of the local (non-AI) parse+extract pipeline for one document — the "New" pages'
/// equivalent of StoredDocument.ParseStatus/SectionExtractStatus, kept in its own table so it never
/// touches the Landing AI-driven fields the old pages rely on. One row per StoredDocument.
///
/// Parse and Extract are two independently-tracked steps, matching the old pages' Parse/Extract-sections
/// split — not one combined status. <see cref="Status"/>/<see cref="Error"/>/<see cref="ParsedAt"/> track
/// Parse (PdfPig/Tesseract -> text with page refs, persisted in <see cref="MarkdownText"/>).
/// <see cref="ExtractStatus"/>/<see cref="ExtractError"/>/<see cref="ExtractedAt"/> track Extract (regex
/// clause/point splitting, run against the already-parsed <see cref="MarkdownText"/> — cheap, instant,
/// re-runnable without touching the PDF again).
/// </summary>
public class NdLocalDocumentExtraction : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid StoredDocumentId { get; set; }

    /// <summary>Which OCR/parse engine produced this row: "tesseract" | "rapidocr". A document can have one
    /// row per engine (unique on StoredDocumentId+Engine), so the same upload can be compared side by side.</summary>
    public string Engine { get; set; } = "tesseract";

    /// <summary>Parse status: pending | processing | parsed | failed.</summary>
    public string Status { get; set; } = "pending";
    public int? TotalPages { get; set; }
    public int? OcrPageCount { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset? ParsedAt { get; set; }
    public Guid? ParsedBy { get; set; }

    /// <summary>Step 1's actual output — parsed text with BCP_PDF_PAGE:N markers. Null until parsed.</summary>
    public string? MarkdownText { get; set; }

    /// <summary>Extract status: pending | processing | extracted | failed.</summary>
    public string ExtractStatus { get; set; } = "pending";
    public int? SectionCount { get; set; }

    /// <summary>Serialized List&lt;LocalSection&gt;-shaped JSON: [{clauseNo, clauseText, sourcePage}].</summary>
    public string SectionsJson { get; set; } = "[]";

    /// <summary>Serialized string[] of warnings — parse warnings until extracted, then extract warnings.</summary>
    public string WarningsJson { get; set; } = "[]";
    public string? ExtractError { get; set; }
    public DateTimeOffset? ExtractedAt { get; set; }

    /// <summary>Semantic extraction — a second, independent extract result alongside the regex-based
    /// one above, run from the same <see cref="MarkdownText"/> without touching it. Splits by embedding
    /// consecutive sentences and cutting where meaning shifts, instead of by numbering — see
    /// docs/pipeline/STRUCTURAL-EXTRACTION-NESTED-NUMBERING-BUG.md for why this exists. Same status
    /// convention: pending | processing | extracted | failed. Never overwrites the structural fields
    /// above, and running one method does not require or affect the other.</summary>
    public string SemanticExtractStatus { get; set; } = "pending";
    public int? SemanticSectionCount { get; set; }

    /// <summary>Same shape as <see cref="SectionsJson"/> — List&lt;LocalSection&gt;-shaped JSON.</summary>
    public string SemanticSectionsJson { get; set; } = "[]";
    public string SemanticWarningsJson { get; set; } = "[]";
    public string? SemanticExtractError { get; set; }
    public DateTimeOffset? SemanticExtractedAt { get; set; }

    /// <summary>Index status: pending | processing | indexed | failed. Set automatically right after
    /// Extract succeeds for an internal document (never for a regulation document — only the internal
    /// side is embedded, see docs/pipeline/HYBRID-ANALYSIS-PIPELINE-PLAN.md Step 0). Tracks the
    /// background job that computes and stores one embedding per row in
    /// <see cref="NdLocalDocumentExtractionSection"/>.</summary>
    public string IndexStatus { get; set; } = "pending";
    public string? IndexError { get; set; }
    public DateTimeOffset? IndexedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Owning workspace (nd_workspaces.id).</summary>
    public Guid? TenantId { get; set; }
}
