using Reguliq.Api.Data.NewDashboard.Entities;

namespace Reguliq.Api.Services.NewDashboard;

/// <summary>
/// Regul.ai reverse coverage INT rows — pipeline.py _reverse_coverage_finding / _reverse_coverage_findings.
/// Non-covered internal sections become gap rows with Clause No. prefixed INT (e.g. INT 7.9-2).
/// </summary>
public static class NdRegulReverseIntRows
{
    public const string IntClausePrefix = "INT ";

    public static bool ShouldCreateIntRow(string? mapping) =>
        !string.Equals(mapping, "covered", StringComparison.OrdinalIgnoreCase);

    public static string FormatIntClauseNo(string sectionRef) =>
        sectionRef.StartsWith(IntClausePrefix, StringComparison.OrdinalIgnoreCase)
            ? sectionRef
            : IntClausePrefix + sectionRef;

    public static NdRegulForwardFinding BuildIntFinding(
        Guid analysisRunId,
        string sectionRef,
        string sectionText,
        string? sourceDoc,
        int? sourcePage,
        string? mapping,
        IReadOnlyList<string> mappedClauseNos,
        IReadOnlyDictionary<string, string> regulatoryByNo,
        bool contradictsRegulation,
        string commentary,
        double confidence)
    {
        var relatedText = mappedClauseNos
            .Select(no => regulatoryByNo.TryGetValue(no, out var t) ? t : null)
            .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));

        var overallStatus = contradictsRegulation ? "non_compliant" : "partial";
        var gapDirection = string.Equals(mapping, "no_regulatory_basis", StringComparison.OrdinalIgnoreCase)
            ? "no_regulatory_basis"
            : "basis_not_verifiable";

        var result = new
        {
            design_status = overallStatus,
            operating_status = overallStatus,
            overall_status = overallStatus,
            confidence,
            interpretation = commentary,
            policy_extract = new[] { sectionText },
            document_reference = sourceDoc ?? "",
            gap_description = BuildGapDescription(mapping, contradictsRegulation, commentary),
            suggested_action = BuildSuggestedAction(mapping, contradictsRegulation),
            gap_direction = gapDirection,
            reverse_mapping = mapping,
            mapped_clause_nos = mappedClauseNos,
            contradicts_regulation = contradictsRegulation,
            source_page = sourcePage,
        };

        return new NdRegulForwardFinding
        {
            AnalysisRunId = analysisRunId,
            AnalysisPointId = null,
            ClauseNo = FormatIntClauseNo(sectionRef),
            ClauseText = relatedText ?? NdRegulPromptDefaults.NoMatchingRegulatoryClause,
            Status = "completed",
            ResultJson = System.Text.Json.JsonSerializer.Serialize(result),
        };
    }

    private static string BuildGapDescription(string? mapping, bool contradicts, string commentary)
    {
        var text = commentary?.Trim() ?? "";
        if (contradicts)
            text += " This also appears to actively CONTRADICT a regulatory requirement.";
        if (string.IsNullOrWhiteSpace(text))
            text = string.Equals(mapping, "no_regulatory_basis", StringComparison.OrdinalIgnoreCase)
                ? "Internal policy content with no matching regulatory clause."
                : "Could not verify regulatory basis for this internal section.";
        return text;
    }

    private static string BuildSuggestedAction(string? mapping, bool contradicts)
    {
        if (contradicts)
            return "Resolve the contradiction with the regulatory requirement; escalate to compliance/legal.";
        if (string.Equals(mapping, "basis_not_verifiable", StringComparison.OrdinalIgnoreCase))
            return "Verify or correct the claimed regulatory basis for this section.";
        return "Confirm this content is intentional operational detail; document its regulatory basis if one exists.";
    }

    public static RegulJudgmentResult ToJudgmentResult(
        string? mapping,
        bool contradictsRegulation,
        double confidence,
        string commentary,
        string sectionText,
        string? sourceDoc)
    {
        var overallStatus = contradictsRegulation ? "non_compliant" : "partial";
        var gapDirection = string.Equals(mapping, "no_regulatory_basis", StringComparison.OrdinalIgnoreCase)
            ? "no_regulatory_basis"
            : "basis_not_verifiable";

        return new RegulJudgmentResult
        {
            DesignStatus = overallStatus,
            OperatingStatus = overallStatus,
            OverallStatus = overallStatus,
            Confidence = confidence,
            Interpretation = commentary?.Trim() ?? "",
            PolicyExtract = string.IsNullOrWhiteSpace(sectionText) ? [] : [sectionText.Trim()],
            DocumentReference = sourceDoc?.Trim() ?? "",
            GapDescription = BuildGapDescription(mapping, contradictsRegulation, commentary),
            SuggestedAction = BuildSuggestedAction(mapping, contradictsRegulation),
            GapDirection = gapDirection,
        };
    }
}
