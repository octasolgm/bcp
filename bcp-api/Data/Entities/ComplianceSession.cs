namespace Reguliq.Api.Data.Entities;

public class ComplianceSession
{
    public Guid Id { get; set; }
    public string SessionKey { get; set; } = string.Empty;
    public string GovFileHash { get; set; } = string.Empty;
    public string InternalFileHash { get; set; } = string.Empty;
    public string? GovFileName { get; set; }
    public string? InternalFileName { get; set; }
    public int TotalGovPoints { get; set; }
    public int ComparedPoints { get; set; }
    public int SkippedPoints { get; set; }
    public string? SkippedJson { get; set; }
    public string ResultsJson { get; set; } = "[]";
    public string? SummaryJson { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
