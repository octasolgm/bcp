using System.Text.Json;
using Reguliq.Api.Data.NewDashboard.Entities;

namespace Reguliq.Api.Services.NewDashboard;

/// <summary>Maps Regul pipeline findings into analysis_points for gap UI + Excel/PDF export.</summary>
public static class NdRegulAnalysisPointSync
{
    public static void ApplyForwardJudgment(
        NdAnalysisPoint point,
        RegulJudgmentResult judgment,
        string landingMessage)
    {
        point.LandingAiStatus = "completed";
        point.LandingAiResult = JsonSerializer.Serialize(new { message = landingMessage });
        point.LandingAiError = null;
        point.LandingAiRunAt = DateTimeOffset.UtcNow;
        point.GoogleAiStatus = "skipped";
        point.GoogleAiResult = null;
        point.GoogleAiError = null;
        point.DualVerifyStatus = "completed";
        point.DualVerifyRunAt = DateTimeOffset.UtcNow;
        point.FinalStatus = NdComplianceParser.NormalizeStatus(
            NdRegulJudgmentFormatter.MapDisplayStatus(judgment.OverallStatus));
        point.LandingAiActionPlan = NdComplianceParser.ExtractActionPlan(landingMessage);
        if (!string.IsNullOrWhiteSpace(point.LandingAiActionPlan))
            point.OriginalAiActionPlan ??= point.LandingAiActionPlan;
        point.UpdatedAt = DateTimeOffset.UtcNow;
    }

    public static void ApplyIntReverseFinding(
        NdAnalysisPoint point,
        string landingMessage,
        RegulJudgmentResult judgment)
    {
        ApplyForwardJudgment(point, judgment, landingMessage);
        point.DualVerifyStatus = "completed";
    }
}
