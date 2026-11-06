using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Reguliq.Api.Data.NewDashboard.Entities;

/// <summary>
/// A support request a workspace user sends to the platform team (an issue, a question, a knowledge request, ...).
/// Owned by the sender's workspace; the platform super admin reads every workspace's tickets from the Support inbox.
/// </summary>
[Table("nd_support_tickets")]
public class NdSupportTicket : ITenantScoped
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Platform-wide running number shown as SUP-00042. Filled by the database.</summary>
    [Column("ticket_no")]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long TicketNo { get; set; }

    [Column("subject")]
    public string Subject { get; set; } = "";

    /// <summary>issue | question | knowledge | feature</summary>
    [Column("category")]
    public string Category { get; set; } = SupportCategories.Question;

    /// <summary>low | normal | high | urgent</summary>
    [Column("priority")]
    public string Priority { get; set; } = SupportPriorities.Normal;

    /// <summary>open | waiting_on_client | resolved | closed</summary>
    [Column("status")]
    public string Status { get; set; } = SupportStatuses.Open;

    [Column("created_by")]
    public Guid? CreatedBy { get; set; }

    [Column("created_by_name")]
    public string? CreatedByName { get; set; }

    [Column("created_by_email")]
    public string? CreatedByEmail { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("last_message_at")]
    public DateTimeOffset LastMessageAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Latest message from the workspace side; newer than StaffLastReadAt = unread for support.</summary>
    [Column("last_client_message_at")]
    public DateTimeOffset? LastClientMessageAt { get; set; }

    /// <summary>Latest visible reply from support; newer than ClientLastReadAt = unread for the workspace.</summary>
    [Column("last_staff_message_at")]
    public DateTimeOffset? LastStaffMessageAt { get; set; }

    [Column("client_last_read_at")]
    public DateTimeOffset? ClientLastReadAt { get; set; }

    [Column("staff_last_read_at")]
    public DateTimeOffset? StaffLastReadAt { get; set; }

    public Guid? TenantId { get; set; }
}

/// <summary>One message in a support ticket's chat. Internal notes are support-only and never sent to the workspace.</summary>
[Table("nd_support_messages")]
public class NdSupportMessage : ITenantScoped
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("ticket_id")]
    public Guid TicketId { get; set; }

    [Column("author_id")]
    public Guid? AuthorId { get; set; }

    [Column("author_name")]
    public string? AuthorName { get; set; }

    /// <summary>True when the platform support team wrote it.</summary>
    [Column("is_staff")]
    public bool IsStaff { get; set; }

    /// <summary>Support-only note; hidden from the workspace.</summary>
    [Column("is_internal")]
    public bool IsInternal { get; set; }

    [Column("body")]
    public string Body { get; set; } = "";

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Guid? TenantId { get; set; }
}

public static class SupportCategories
{
    public const string Issue = "issue";
    public const string Question = "question";
    public const string Knowledge = "knowledge";
    public const string Feature = "feature";
    public static readonly string[] All = [Issue, Question, Knowledge, Feature];
}

public static class SupportPriorities
{
    public const string Low = "low";
    public const string Normal = "normal";
    public const string High = "high";
    public const string Urgent = "urgent";
    public static readonly string[] All = [Low, Normal, High, Urgent];
}

public static class SupportStatuses
{
    public const string Open = "open";
    public const string WaitingOnClient = "waiting_on_client";
    public const string Resolved = "resolved";
    public const string Closed = "closed";
    public static readonly string[] All = [Open, WaitingOnClient, Resolved, Closed];
}
