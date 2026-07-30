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
        var capFromMessage = NdComplianceParser.ExtractActionPlan(landingMessage);
        var capFromJudgment = !string.IsNullOrWhiteSpace(judgment.SuggestedAction)
            ? judgment.SuggestedAction.Trim()
            : judgment.GapDescription?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(capFromJudgment) || capFromJudgment is "N/A" or "—" or "-")
            capFromJudgment = capFromMessage ?? "";
        if (!string.IsNullOrWhiteSpace(capFromJudgment) && capFromJudgment is not ("N/A" or "—" or "-"))
        {
            point.LandingAiActionPlan = capFromJudgment;
            point.OriginalAiActionPlan ??= capFromJudgment;
        }
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
