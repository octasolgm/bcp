namespace Reguliq.Api.Data.Entities;

public class DualVerifySession
{
    public Guid Id { get; set; }
    public string Status { get; set; } = "queued";
    public string Granularity { get; set; } = "leaf";
    public string GovDocId { get; set; } = "gov-tfs-guidelines";
    public string InternalDocId { get; set; } = "internal-imptfs";
    public string GovFileHash { get; set; } = string.Empty;
    public string InternalFileHash { get; set; } = string.Empty;
    public string? GovFileName { get; set; }
    public string? InternalFileName { get; set; }
    public int TotalPoints { get; set; }
    public int CompletedPoints { get; set; }
    public int FailedPoints { get; set; }
    public int RunningPoints { get; set; }
    public int QueuedPoints { get; set; }
    public string? Phase2Model { get; set; }
    public string Pipeline { get; set; } = "kafka-dual-verify";
    public string? ComplianceSessionKey { get; set; }
    public string? SummaryJson { get; set; }
    public string Transport { get; set; } = "local";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    public ICollection<DualVerifyPointJob> PointJobs { get; set; } = new List<DualVerifyPointJob>();
}
