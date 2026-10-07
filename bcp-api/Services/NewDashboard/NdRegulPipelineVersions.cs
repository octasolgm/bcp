namespace Reguliq.Api.Services.NewDashboard;

/// <summary>
/// Versions of the hybrid retrieval pipeline (Steps 1-6). The version in use is an admin setting
/// (Admin -> Analysis prompts), recorded on every clause's retrieval and on the run, and kept in evals, so a
/// change that gives worse results can be switched back. Behaviour per version lives in
/// <see cref="RegulEmbeddingRetrievalService"/>; add a new number here for every behaviour change.
/// </summary>
public static class NdRegulPipelineVersions
{
    public sealed record Info(int Version, string Label, string Description);

    public const int V1 = 1;
    public const int V2ExpandedWording = 2;

    /// <summary>Used when no admin choice is stored.</summary>
    public const int Default = V2ExpandedWording;

    public static readonly IReadOnlyList<Info> All =
    [
        new(V1, "v1 - original",
            "Each sub-obligation is searched once (BM25 + embedding) with its text plus the query-expansion terms appended at the end."),
        new(V2ExpandedWording, "v2 - expanded wording search",
            "v1, plus a second search per sub-obligation with every acronym and synonym in it swapped for its counterpart "
            + "(e.g. CDD -> customer due diligence and back), so sections written in the other form are found by BM25 and embedding alike."),
    ];

    public static bool IsKnown(int version) => All.Any(v => v.Version == version);

    public static string Label(int version) => All.FirstOrDefault(v => v.Version == version)?.Label ?? $"v{version}";
}
