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
        var corrective = !string.IsNullOrWhiteSpace(judgment.SuggestedAction)
            ? judgment.SuggestedAction.Trim()
            : !string.IsNullOrWhiteSpace(judgment.GapDescription)
                ? judgment.GapDescription.Trim()
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
        if (s.Contains("partial")) return "Partial compliant";
        if (s.Contains("non")) return "Non-Compliant";
        return string.IsNullOrEmpty(s) ? "Non-Compliant" : overallStatus!.Trim();
    }

    private static string BuildPolicyResponse(RegulJudgmentResult judgment)
    {
        if (judgment.PolicyExtract.Count > 0)
            return string.Join("\n", judgment.PolicyExtract.Where(s => !string.IsNullOrWhiteSpace(s)));

        if (!string.IsNullOrWhiteSpace(judgment.Interpretation))
            return judgment.Interpretation.Trim();

        if (!string.IsNullOrWhiteSpace(judgment.DocumentReference))
            return $"See {judgment.DocumentReference.Trim()}.";

        return "No corresponding procedure found.";
    }
}
