using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Reguliq.Api.Data.NewDashboard.Entities;

/// <summary>
/// A workspace's connection to one cloud storage account (Google Drive, OneDrive, ...). Only the refresh token is
/// kept, encrypted; access tokens are short lived and fetched on demand. One connection per provider per workspace.
/// </summary>
[Table("nd_cloud_connections")]
public class NdCloudConnection : ITenantScoped
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>google | microsoft | dropbox | box | pcloud</summary>
    [Column("provider")]
    public string Provider { get; set; } = "";

    [Column("account_id")]
    public string? AccountId { get; set; }

    /// <summary>What the user sees, e.g. the account email.</summary>
    [Column("account_label")]
    public string? AccountLabel { get; set; }

    /// <summary>Refresh token encrypted by CloudSecrets. Never sent to the browser.</summary>
    [Column("refresh_token_enc")]
    public string RefreshTokenEnc { get; set; } = "";

    [Column("scopes")]
    public string? Scopes { get; set; }

    /// <summary>connected | needs_reconnect</summary>
    [Column("status")]
    public string Status { get; set; } = CloudConnectionStatuses.Connected;

    [Column("last_error")]
    public string? LastError { get; set; }

    [Column("connected_by")]
    public Guid? ConnectedBy { get; set; }

    [Column("connected_at")]
    public DateTimeOffset ConnectedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("last_used_at")]
    public DateTimeOffset? LastUsedAt { get; set; }

    public Guid? TenantId { get; set; }
}

/// <summary>One file imported (or skipped) from a connected account: who, what, from where.</summary>
[Table("nd_cloud_import_log")]
public class NdCloudImportLog : ITenantScoped
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("connection_id")]
    public Guid? ConnectionId { get; set; }

    [Column("provider")]
    public string Provider { get; set; } = "";

    [Column("account_label")]
    public string? AccountLabel { get; set; }

    [Column("provider_file_id")]
    public string ProviderFileId { get; set; } = "";

    [Column("file_name")]
    public string FileName { get; set; } = "";

    /// <summary>internal | regulation</summary>
    [Column("kind")]
    public string Kind { get; set; } = "internal";

    [Column("stored_document_id")]
    public Guid? StoredDocumentId { get; set; }

    /// <summary>imported | duplicate | failed</summary>
    [Column("result")]
    public string Result { get; set; } = "imported";

    [Column("error")]
    public string? Error { get; set; }

    [Column("imported_by")]
    public Guid? ImportedBy { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Guid? TenantId { get; set; }
}

public static class CloudConnectionStatuses
{
    public const string Connected = "connected";
    public const string NeedsReconnect = "needs_reconnect";
}
