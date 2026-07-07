namespace Reguliq.Api.Services.LandingAi;

public sealed class ComplianceComparisonResult
{
    public string OutputResponse { get; set; } = "";
    public string Status { get; set; } = "Non-Compliant";
    public int Confidence { get; set; }
    public string? FulfilledClauses { get; set; }
    public string? CorrectiveAction { get; set; }
    public string? Responsibility { get; set; }
    public string? ReferencePdf { get; set; }
}

public sealed class ParseCacheRow
{
    public string Markdown { get; set; } = "";
    public string FileName { get; set; } = "";
}

public sealed class ExtractCacheRow
{
    public string PointsJson { get; set; } = "";
}
