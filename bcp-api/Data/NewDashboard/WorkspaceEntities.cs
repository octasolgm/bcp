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
