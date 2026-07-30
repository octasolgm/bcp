namespace Reguliq.Api.Services.NewDashboard;

public sealed class RegulJudgmentResult
{
    public string DesignStatus { get; set; } = "";
    public string OperatingStatus { get; set; } = "";
    public string OverallStatus { get; set; } = "";
    public double Confidence { get; set; }
    public string Interpretation { get; set; } = "";
    public List<string> PolicyExtract { get; set; } = [];
    public string DocumentReference { get; set; } = "";
    public string GapDescription { get; set; } = "";
    public string SuggestedAction { get; set; } = "";
    public string GapDirection { get; set; } = "";
}

public sealed class RegulReverseMappingResult
{
    public List<string> MappedClauseNos { get; set; } = [];
    public string Mapping { get; set; } = "";
    public string Commentary { get; set; } = "";
    public double Confidence { get; set; }
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
