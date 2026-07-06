using Reguliq.Api.Models;

namespace Reguliq.Api.Services;

public class DashboardService
{
    public DashboardMetricsDto GetSeedMetrics() => new(
        CriticalGaps: 3,
        HighRisk: 7,
        TotalFindings: 22,
        CompliantItems: 6,
        LastAnalysisDate: DateTime.UtcNow.AddDays(-2).ToString("yyyy-MM-dd"),
        RiskBreakdown:
        [
            new("Critical", 3, "#ef4444"),
            new("High", 7, "#f97316"),
            new("Medium", 6, "#eab308"),
            new("Low", 4, "#3b82f6"),
            new("Compliant", 6, "#22c55e")
        ],
        RemediationItems:
        [
            new("2.1.1 Customer due diligence", "critical", "2026-07-15", "In Progress"),
            new("2.3.4 STR reporting timeline", "high", "2026-07-30", "Open"),
            new("3.2.1 PEP screening", "high", "2026-08-01", "Open"),
            new("4.1.2 Record retention", "medium", "2026-08-15", "Planned")
        ],
        RecentAnalyses:
        [
            new("demo-session-001", "TFS Guidelines Gap Analysis", "2026-06-28", 22, 3, 7)
        ]);
}
