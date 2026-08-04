using Reguliq.Api.Services.NewDashboard;
using Xunit;

namespace Reguliq.Api.Tests;

public class NdRegulLlmJsonHelperTests
{
    [Fact]
    public void ParseJsonObject_maps_snake_case_judgment_fields()
    {
        const string json = """
            {
              "overall_status": "partial_compliant",
              "design_status": "compliant",
              "operating_status": "non_compliant",
              "confidence": 0.82,
              "interpretation": "Policy addresses the requirement in part.",
              "policy_extract": ["Annual review is documented in section 4.2."],
              "document_reference": "Manual.pdf p.12",
              "gap_description": "Operating controls are not fully described.",
              "suggested_action": "Update procedures.",
              "gap_direction": "policy_gap"
            }
            """;

        var result = NdRegulLlmJsonHelper.ParseJsonObject<RegulJudgmentResult>(json);

        Assert.Equal("partial_compliant", result.OverallStatus);
        Assert.Equal("compliant", result.DesignStatus);
        Assert.Equal("non_compliant", result.OperatingStatus);
        Assert.Equal(0.82, result.Confidence);
        Assert.Equal("Policy addresses the requirement in part.", result.Interpretation);
        Assert.Single(result.PolicyExtract);
        Assert.Equal("Annual review is documented in section 4.2.", result.PolicyExtract[0]);
        Assert.Equal("Manual.pdf p.12", result.DocumentReference);
        Assert.Equal("Operating controls are not fully described.", result.GapDescription);
        Assert.Equal("policy_gap", result.GapDirection);
    }

    [Fact]
    public void ParseJsonObject_maps_snake_case_reverse_mapping_fields()
    {
        const string json = """
            {
              "mapped_clause_nos": ["3.9", "3.10"],
              "mapping": "partial",
              "commentary": "Section overlaps two clauses.",
              "confidence": 0.65,
              "contradicts_regulation": false
            }
            """;

        var result = NdRegulLlmJsonHelper.ParseJsonObject<RegulReverseMappingResult>(json);

        Assert.Equal(["3.9", "3.10"], result.MappedClauseNos);
        Assert.Equal("partial", result.Mapping);
        Assert.Equal("Section overlaps two clauses.", result.Commentary);
        Assert.Equal(0.65, result.Confidence);
        Assert.False(result.ContradictsRegulation);
    }

    [Fact]
    public void ParseJudgmentResult_accepts_policy_extract_as_string()
    {
        const string json = """
            {
              "overall_status": "partial",
              "design_status": "partial",
              "operating_status": "partial",
              "confidence": 0.75,
              "interpretation": "Section 7.4 mentions occasional transactions.",
              "policy_extract": "Occasional transactions must be reviewed within 24 hours.",
              "document_reference": "Manual.pdf section 7.4",
              "gap_description": "Operating controls are thin.",
              "suggested_action": "Expand procedures.",
              "gap_direction": "missing_in_internal"
            }
            """;

        var result = NdRegulLlmJsonHelper.ParseJudgmentResult(json);

        Assert.Equal("partial", result.OverallStatus);
        Assert.Single(result.PolicyExtract);
        Assert.Contains("Occasional transactions", result.PolicyExtract[0]);
    }
}
