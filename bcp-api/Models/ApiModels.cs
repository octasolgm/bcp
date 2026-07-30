namespace Reguliq.Api.Models;

public record ApiResponse<T>(bool Success, T Data, string? Message = null);

public record GovPoint(
    string PointId,
    string? Title,
    string Text,
    string? Section,
    int? PageHint = null,
    string? PointType = null);

public record DualVerifyAgreementDto(
    string Status,
    string Label,
    string LandingStatus,
    string LlmStatus,
    int? LandingConfidence,
    int? LlmConfidence,
    int? ConfidenceDelta,
    string Summary);

public record CreateDualVerifyJobRequest(
    List<string> PointIds,
    string Granularity = "leaf",
    string GovDocId = "gov-tfs-guidelines",
    string InternalDocId = "internal-imptfs",
    string Phase2Model = "gemini-3.5-flash",
    bool ForceRefresh = false);

public record DualVerifyJobMessage(
    string MessageId,
    Guid JobId,
    Guid SessionId,
    string PointId,
    string? PointTitle,
    string GovText,
    string Granularity,
    string GovDocId,
    string InternalDocId,
    string GovFileHash,
    string InternalFileHash,
    string GovFileName,
    string InternalFileName,
    string Phase2Model,
    int Attempt,
    int MaxAttempts,
    bool ForceRefresh,
    string CorrelationId,
    DateTime CreatedAt,
    string SchemaVersion = "1.0",
    string? Transport = null);

public record DashboardMetricsDto(
    int Compliant,
    int Partial,
    int NonCompliant,
    int TotalFindings,
    string LastAnalysisDate,
    List<ComplianceBreakdownItem> ComplianceBreakdown,
    List<RecentAnalysisDto> RecentAnalyses);

public record ComplianceBreakdownItem(string Name, int Value, string Color);
public record RecentAnalysisDto(string Id, string Title, string Date, int Findings, int Compliant, int Partial, int NonCompliant);

public record ClientGovPointDto(string PointId, string? Title, string Text, string? Section);

public record DualVerifyHealthDto(
    string Status,
    string Transport,
    bool KafkaConfigured,
    KafkaTopicsDto Topics,
    PersistenceDto Persistence);

public record KafkaTopicsDto(string Jobs, string Retry, string Dlq, string Results);
public record PersistenceDto(
    bool DualVerifyTablesReady,
    bool ComplianceSessionsTableReady,
    bool FileFallbackReady,
    string FileDataDir,
    string Mode,
    string? Hint);

public record SessionProgressDto(DualVerifySessionDto Session, List<PointJobDto> Points);
public record DualVerifySessionDto(
    Guid Id, string Status, int TotalPoints, int CompletedPoints, int FailedPoints,
    int RunningPoints, int QueuedPoints, string Transport, string Phase2Model,
    string Granularity, DateTime UpdatedAt,
    string? GovFileName = null,
    string? InternalFileName = null,
    string? GovFileHash = null,
    string? InternalFileHash = null,
    Guid? RegulationDocumentId = null,
    Guid? InternalDocumentId = null);
public record PointJobDto(
    Guid Id, string PointId, string? PointTitle, string Status,
    string? LandingMessage, string? LlmMessage, DualVerifyAgreementDto? AgreementJson,
    string? ErrorMessage, string? RunningStage = null);
