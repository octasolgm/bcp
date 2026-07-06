namespace Reguliq.Api.Data.Entities;

public class DualVerifyPointJob
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public DualVerifySession Session { get; set; } = null!;
    public string PointId { get; set; } = string.Empty;
    public string? PointTitle { get; set; }
    public string GovText { get; set; } = string.Empty;
    public string Status { get; set; } = "queued";
    public int Attempt { get; set; } = 1;
    public int MaxAttempts { get; set; } = 3;
    public string? LandingMessage { get; set; }
    public string? LlmMessage { get; set; }
    public string? AgreementJson { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
