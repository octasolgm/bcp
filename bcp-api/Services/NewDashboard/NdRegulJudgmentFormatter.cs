namespace Reguliq.Api.Services.NewDashboard;

/// <summary>Formats Regul judgment JSON into the reference-compliance message shape used by gap UI + export.</summary>
public static class NdRegulJudgmentFormatter
{
    public static string FormatLandingMessage(string clauseNo, string clauseText, RegulJudgmentResult judgment)
    {
        var status = MapDisplayStatus(judgment.OverallStatus);
        var confidencePct = (int)Math.Round(Math.Clamp(judgment.Confidence, 0, 1) * 100);
        var policyResponse = judgment.PolicyExtract.Count > 0
            ? string.Join("\n", judgment.PolicyExtract.Where(s => !string.IsNullOrWhiteSpace(s)))
            : "No corresponding procedure found.";
        var fulfilled = status == "Compliant" ? "All required elements addressed." : "None";
        var corrective = !string.IsNullOrWhiteSpace(judgment.SuggestedAction)
            ? judgment.SuggestedAction.Trim()
            : status == "Compliant" ? "N/A" : judgment.GapDescription.Trim();
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

    public static string MapDisplayStatus(string? overallStatus)
    {
        var s = (overallStatus ?? "").Trim().ToLowerInvariant();
        if (s == "compliant") return "Compliant";
        if (s.Contains("partial")) return "Partial compliant";
        if (s.Contains("non")) return "Non-Compliant";
        return "Non-Compliant";
    }
}
