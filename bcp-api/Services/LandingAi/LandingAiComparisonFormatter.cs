using Reguliq.Api.Models;

namespace Reguliq.Api.Services.LandingAi;

/// <summary>Formats Landing AI compare output for dual-verify Phase 1.</summary>
public static class LandingAiComparisonFormatter
{
    public static string FormatMessage(GovPoint point, string internalFileName, ComplianceComparisonResult comparison)
    {
        var head = string.Join(' ', new[] { point.PointId, point.Title }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var status = string.IsNullOrWhiteSpace(comparison.Status) ? "Non-Compliant" : comparison.Status.Trim();
        var confidence = comparison.Confidence;
        var corrective = !string.IsNullOrWhiteSpace(comparison.CorrectiveAction)
            ? comparison.CorrectiveAction!.Trim()
            : status == "Compliant" ? "N/A" : "—";
        var responsibility = !string.IsNullOrWhiteSpace(comparison.Responsibility)
            ? comparison.Responsibility!.Trim()
            : status == "Compliant" ? "N/A" : "—";

        return string.Join('\n', new[]
        {
            head,
            point.Text,
            "",
            "Reference PDF :",
            !string.IsNullOrWhiteSpace(comparison.ReferencePdf) ? comparison.ReferencePdf.Trim() : internalFileName,
            "",
            "Output/Response :",
            !string.IsNullOrWhiteSpace(comparison.OutputResponse) ? comparison.OutputResponse.Trim() : "No corresponding procedure found.",
            "",
            "Fulfilled clauses :",
            !string.IsNullOrWhiteSpace(comparison.FulfilledClauses) ? comparison.FulfilledClauses.Trim() : "None",
            "",
            $"Comply Yes/No (Status) : {status}",
            $"Compliance Confidence % : {confidence}%",
            "Corrective Action Plan :",
            corrective,
            "Responsibility :",
            responsibility,
        });
    }
}
