using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Reguliq.Api.Data.NewDashboard.Entities;

/// <summary>
/// A client organisation (e.g. one bank). Every tenant-owned row carries <c>tenant_id</c> pointing here,
/// and AppDbContext filters all of them to the request's workspace (see <c>WorkspaceScope</c>).
/// Named "workspace" in the product; the column is <c>tenant_id</c> because <c>stored_documents</c>
/// already has an unrelated legacy text column called <c>workspace_id</c>.
/// </summary>
[Table("nd_workspaces")]
public class NdWorkspace
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("name")]
    public string Name { get; set; } = "";

    [Column("slug")]
    public string Slug { get; set; } = "";

    [Column("description")]
    public string? Description { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    /// <summary>Warn the workspace once this share of its granted AI credits is left (default 20%).</summary>
    [Column("ai_credit_low_threshold_pct")]
    public int AiCreditLowThresholdPct { get; set; } = 20;

    /// <summary>Hard cap on total credits this workspace may ever consume. Null = no limit.</summary>
    [Column("ai_credit_limit")]
    public decimal? AiCreditLimit { get; set; }

    /// <summary>Hard cap on the total number of clauses this workspace may ever have analysed. Null = no
    /// limit. Independent of the AI credit limit: either, both or neither can be set.</summary>
    [Column("clause_limit")]
    public int? ClauseLimit { get; set; }

    /// <summary>Per-workspace markup override. Null = use the platform default markup.</summary>
    [Column("ai_markup_override")]
    public decimal? AiMarkupOverride { get; set; }

    /// <summary>Which country's date convention this workspace's UI displays dates in (see
    /// NdDateFormats on the frontend). Null = platform default (UAE, DD/MM/YYYY).</summary>
    [Column("date_format_region")]
    public string? DateFormatRegion { get; set; }

    /// <summary>Actions module: when off, corrective actions are hidden in this workspace's reports. Gaps and
    /// actions are still produced and stored exactly as before; this only controls what is shown.</summary>
    [Column("actions_enabled")]
    public bool ActionsEnabled { get; set; } = true;

    /// <summary>Embeddings module: when off, finalizing a report does not generate corrected (embedded)
    /// copies of the internal documents.</summary>
    [Column("embeddings_enabled")]
    public bool EmbeddingsEnabled { get; set; } = true;

    /// <summary>Upload limits in MB, set by the platform admin. Null = no workspace limit (the server's own
    /// 50 MB per-file cap still applies).</summary>
    [Column("max_document_mb")]
    public int? MaxDocumentMb { get; set; }

    /// <summary>Largest combined size of the files chosen in one multi-file upload.</summary>
    [Column("max_upload_mb")]
    public int? MaxUploadMb { get; set; }

    /// <summary>Total size of all documents this business may keep in its library.</summary>
    [Column("max_storage_mb")]
    public int? MaxStorageMb { get; set; }

    [Column("created_by")]
    public Guid? CreatedBy { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Row owned by one workspace. The column is filled on insert (EF, then a DB trigger fallback).</summary>
public interface ITenantScoped
{
    Guid? TenantId { get; set; }
}
