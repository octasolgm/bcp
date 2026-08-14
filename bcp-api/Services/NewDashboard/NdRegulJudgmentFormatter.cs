using System.Text.RegularExpressions;

namespace Reguliq.Api.Services.NewDashboard;

/// <summary>Formats Regul judgment JSON into the reference-compliance message shape used by gap UI + export.</summary>
public static class NdRegulJudgmentFormatter
{
    public static string FormatLandingMessage(string clauseNo, string clauseText, RegulJudgmentResult judgment)
    {
        var status = MapDisplayStatus(judgment.OverallStatus, judgment.DesignStatus);
        var confidencePct = (int)Math.Round(Math.Clamp(judgment.Confidence, 0, 1) * 100);
        var policyResponse = BuildPolicyResponse(judgment);
        var fulfilled = status == "Compliant" ? "All required elements addressed." : "None";
        var gapAnalysis = BuildGapAnalysisText(judgment, status);
        var corrective = !string.IsNullOrWhiteSpace(judgment.SuggestedAction)
            ? judgment.SuggestedAction.Trim()
            : status == "Compliant" ? "N/A" : "";
        if (string.IsNullOrWhiteSpace(corrective))
            corrective = status == "Compliant" ? "N/A" : "—";

        var reference = !string.IsNullOrWhiteSpace(judgment.DocumentReference)
            ? judgment.DocumentReference.Trim()
            : "Internal policy manual";

        return string.Join('\n', new[]
        {
            clauseNo,
            clauseText,
            "",
            "Reference PDF :",
            reference,
            "",
            "Document Reference :",
            reference,
            "",
            "Output/Response :",
            policyResponse,
            "",
            "Fulfilled clauses :",
            fulfilled,
            "",
            $"Comply Yes/No (Status) : {status}",
            $"Compliance Confidence % : {confidencePct}%",
            "Gap analysis :",
            string.IsNullOrWhiteSpace(gapAnalysis) ? (status == "Compliant" ? "N/A" : "—") : gapAnalysis,
            "Corrective Action Plan :",
            corrective,
            "Responsibility :",
            status == "Compliant" ? "N/A" : "Compliance / policy owner",
        });
    }

    public static string MapDisplayStatus(string? overallStatus, string? designStatus = null)
    {
        var s = (overallStatus ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(s) && !string.IsNullOrWhiteSpace(designStatus))
            s = designStatus.Trim().ToLowerInvariant();
        if (s == "compliant") return "Compliant";
        if (s.Contains("partial")) return "Partial Compliant";
        if (s.Contains("non")) return "Non Compliant";
        return string.IsNullOrEmpty(s) ? "Non Compliant" : overallStatus!.Trim();
    }

    private static string BuildGapAnalysisText(RegulJudgmentResult judgment, string status)
    {
        if (!string.IsNullOrWhiteSpace(judgment.GapDescription))
            return judgment.GapDescription.Trim();

        if (status == "Compliant")
            return "";

        if (!string.IsNullOrWhiteSpace(judgment.Interpretation))
            return ExtractGapFromInterpretation(judgment.Interpretation);

        return "";
    }

    /// <summary>Derive gap text for demo seed rows (interpretation holds element-level gaps).</summary>
    public static string ResolveGapDescriptionForSeedRow(
        string? gapDescription,
        string? interpretation,
        string? overallStatus,
        string? designStatus = null)
    {
        if (!string.IsNullOrWhiteSpace(gapDescription))
            return gapDescription.Trim();

        var status = MapDisplayStatus(overallStatus, designStatus);
        if (status == "Compliant")
            return "";

        return ExtractGapFromInterpretation(interpretation);
    }

    /// <summary>
    /// Demo seed interpretation: return the full assessment text (regulator preamble + elements),
    /// matching Excel / cbuae-aml-demo-judgments.json exports.
    /// </summary>
    public static string ExtractGapFromInterpretation(string? interpretation)
    {
        return string.IsNullOrWhiteSpace(interpretation) ? "" : interpretation.Trim();
    }

    private static string BuildPolicyResponse(RegulJudgmentResult judgment)
    {
        if (judgment.PolicyExtract.Count > 0)
        {
            return judgment.PolicyExtract.Count == 1
                ? judgment.PolicyExtract[0].Trim()
                : string.Join(
                    "\n\n",
                    judgment.PolicyExtract
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Select((s, i) => $"({i + 1}) {s.Trim()}"));
        }

        if (!string.IsNullOrWhiteSpace(judgment.DocumentReference))
            return $"See {judgment.DocumentReference.Trim()}.";

        return "No corresponding procedure found.";
    }
}
