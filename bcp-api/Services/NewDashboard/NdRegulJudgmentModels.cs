using System.Text.Json.Serialization;

namespace Reguliq.Api.Services.NewDashboard;

public sealed class RegulJudgmentResult
{
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
    [JsonConverter(typeof(JsonStringOrArrayConverter))]
    public List<string> PolicyExtract { get; set; } = [];

    [JsonPropertyName("document_reference")]
    public string DocumentReference { get; set; } = "";

    [JsonPropertyName("gap_description")]
    public string GapDescription { get; set; } = "";

    [JsonPropertyName("suggested_action")]
    public string SuggestedAction { get; set; } = "";

    [JsonPropertyName("gap_direction")]
    public string GapDirection { get; set; } = "";
}

public sealed class RegulReverseMappingResult
{
    [JsonPropertyName("mapped_clause_nos")]
    public List<string> MappedClauseNos { get; set; } = [];

    [JsonPropertyName("mapping")]
    public string Mapping { get; set; } = "";

    [JsonPropertyName("commentary")]
    public string Commentary { get; set; } = "";

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    [JsonPropertyName("contradicts_regulation")]
    public bool ContradictsRegulation { get; set; }
}

public sealed class RegulQualitativeDimension
{
    public string Dimension { get; set; } = "";
    public string Rating { get; set; } = "";
    public string Commentary { get; set; } = "";
    public List<string> Examples { get; set; } = [];
}

public sealed class RegulQualitativeResult
{
    public string OverallRating { get; set; } = "";
    public List<RegulQualitativeDimension> Dimensions { get; set; } = [];
    public List<string> Strengths { get; set; } = [];
    public List<string> ImprovementRecommendations { get; set; } = [];
}
