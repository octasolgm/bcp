using System.Text.Json;
using System.Text.Json.Serialization;
using Reguliq.Api.Services.NewDashboard;
using Xunit;

namespace Reguliq.Api.Tests;

public class NdRegulJudgmentFormatterTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed class SeedRow
    {
        [JsonPropertyName("clause_no")]
        public string ClauseNo { get; set; } = "";

        [JsonPropertyName("clause_content")]
        public string? ClauseContent { get; set; }

        [JsonPropertyName("design_status")]
        public string DesignStatus { get; set; } = "";

        [JsonPropertyName("operating_status")]
        public string OperatingStatus { get; set; } = "";

        [JsonPropertyName("overall_status")]
        public string OverallStatus { get; set; } = "";

        [JsonPropertyName("confidence")]
        public double Confidence { get; set; }

        [JsonPropertyName("interpretation")]
        public string Interpretation { get; set; } = "";

        [JsonPropertyName("policy_extract")]
        public List<string> PolicyExtract { get; set; } = [];

        [JsonPropertyName("document_reference")]
        public string DocumentReference { get; set; } = "";

        [JsonPropertyName("gap_description")]
        public string GapDescription { get; set; } = "";

        [JsonPropertyName("suggested_action")]
        public string SuggestedAction { get; set; } = "";

        [JsonPropertyName("gap_direction")]
        public string GapDirection { get; set; } = "";

        public RegulJudgmentResult ToJudgmentResult()
        {
            var gapDescription = NdRegulJudgmentFormatter.ResolveGapDescriptionForSeedRow(
                GapDescription,
                Interpretation,
                OverallStatus,
                DesignStatus);

            return new RegulJudgmentResult
            {
                DesignStatus = DesignStatus,
                OperatingStatus = OperatingStatus,
                OverallStatus = OverallStatus,
                Confidence = Confidence,
                Interpretation = Interpretation,
                PolicyExtract = PolicyExtract,
                DocumentReference = DocumentReference,
                GapDescription = gapDescription,
                SuggestedAction = SuggestedAction,
                GapDirection = GapDirection,
            };
        }
    }

    private static List<SeedRow> LoadSeedRows()
    {
        var path = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "SeedData", "cbuae-aml-demo-judgments.json"));
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<List<SeedRow>>(json, JsonOptions) ?? [];
    }

    private static string ExtractGapAnalysisField(string landingMessage)
    {
        const string marker = "Gap analysis :";
        var idx = landingMessage.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return "";
        var after = landingMessage[(idx + marker.Length)..];
        var capIdx = after.IndexOf("Corrective Action Plan :", StringComparison.Ordinal);
        if (capIdx >= 0) after = after[..capIdx];
        return after.Trim();
    }

    [Fact]
    public void Clause_3_2_gap_analysis_matches_seed_interpretation()
    {
        var row = LoadSeedRows().First(r => r.ClauseNo == "3.2");
        var judgment = row.ToJudgmentResult();
        var message = NdRegulJudgmentFormatter.FormatLandingMessage(
            row.ClauseNo,
            row.ClauseContent ?? "",
            judgment);

        var gapFromMessage = ExtractGapAnalysisField(message);
        var expected = row.Interpretation.Trim();

        Assert.Equal(expected, gapFromMessage);
        Assert.Equal(expected, judgment.GapDescription.Trim());
        Assert.Equal(expected, NdRegulJudgmentFormatter.ExtractGapFromInterpretation(row.Interpretation));
    }

    [Fact]
    public void All_non_compliant_seed_rows_gap_field_matches_json_source()
    {
        var rows = LoadSeedRows();
        var failures = new List<string>();

        foreach (var row in rows)
        {
            var status = NdRegulJudgmentFormatter.MapDisplayStatus(row.OverallStatus, row.DesignStatus);
            if (status == "Compliant") continue;

            var judgment = row.ToJudgmentResult();
            var message = NdRegulJudgmentFormatter.FormatLandingMessage(
                row.ClauseNo,
                row.ClauseContent ?? "",
                judgment);

            var gapFromMessage = ExtractGapAnalysisField(message);
            var explicitGap = row.GapDescription.Trim();
            var expected = !string.IsNullOrWhiteSpace(explicitGap)
                ? explicitGap
                : row.Interpretation.Trim();

            if (string.IsNullOrWhiteSpace(expected))
            {
                failures.Add($"§{row.ClauseNo}: no gap_description or interpretation");
                continue;
            }

            if (gapFromMessage != expected)
                failures.Add($"§{row.ClauseNo}: landing gap field length {gapFromMessage.Length} != expected {expected.Length}");
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }
}
