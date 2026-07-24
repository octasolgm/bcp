using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Reguliq.Api.Data.Entities;

[Table("stored_documents")]
public class StoredDocument
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("title")]
    public string Title { get; set; } = "";

    [Column("original_file_name")]
    public string OriginalFileName { get; set; } = "";

    [Column("file_type")]
    public string FileType { get; set; } = "PDF";

    [Column("category")]
    public string Category { get; set; } = "AML/CFT";

    [Column("filter_key")]
    public string FilterKey { get; set; } = "aml";

    [Column("doc_kind")]
    public string DocKind { get; set; } = "document"; // document | regulation

    [Column("version")]
    public string Version { get; set; } = "v1";

    [Column("version_number")]
    public int VersionNumber { get; set; } = 1;

    [Column("status")]
    public string Status { get; set; } = "review-due";

    [Column("gap_count")]
    public int? GapCount { get; set; }

    [Column("pages")]
    public int Pages { get; set; }

    [Column("size_bytes")]
    public long SizeBytes { get; set; }

    [Column("content_type")]
    public string ContentType { get; set; } = "application/octet-stream";

    [Column("storage_bucket")]
    public string StorageBucket { get; set; } = "doc";

    [Column("storage_path")]
    public string StoragePath { get; set; } = "";

    /// <summary>Original Word upload path when StoragePath holds converted PDF for Landing AI.</summary>
    [Column("source_storage_path")]
    public string? SourceStoragePath { get; set; }

    /// <summary>SHA-256 of file bytes — key into Landing AI extract cache.</summary>
    [Column("file_hash")]
    public string? FileHash { get; set; }

    [Column("point_count")]
    public int? PointCount { get; set; }

    /// <summary>Internal PDF parse state: pending | processing | parsed | failed</summary>
    [Column("parse_status")]
    public string ParseStatus { get; set; } = "pending";

    [Column("parsed_at")]
    public DateTimeOffset? ParsedAt { get; set; }

    [Column("parse_error")]
    public string? ParseError { get; set; }

    [Column("uploaded_by")]
    public Guid? UploadedBy { get; set; }

    [Column("parsed_by")]
    public Guid? ParsedBy { get; set; }

    /// <summary>Soft-deleted from ND document library (row kept in DB).</summary>
    [Column("is_hidden")]
    public bool IsHidden { get; set; }

    [Column("hidden_at")]
    public DateTimeOffset? HiddenAt { get; set; }

    [Column("hidden_by")]
    public Guid? HiddenBy { get; set; }

    [Column("workspace_id")]
    public string WorkspaceId { get; set; } = "snb-uae-difc";

    [Column("history_json")]
    public string HistoryJson { get; set; } = "[]";

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
