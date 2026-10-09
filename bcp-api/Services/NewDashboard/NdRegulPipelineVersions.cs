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
    public const int V3RelevanceSelection = 3;
    public const int V4Passages = 4;
    public const int V5GapVerification = 5;
    public const int V6RequirementJudgment = 6;

    /// <summary>Used when no admin choice is stored.</summary>
    public const int Default = V2ExpandedWording;

    public static readonly IReadOnlyList<Info> All =
    [
        new(V1, "v1 - original",
            "Each sub-obligation is searched once (BM25 + embedding) with its text plus the query-expansion terms appended at the end."),
        new(V2ExpandedWording, "v2 - expanded wording search",
            "v1, plus a second search per sub-obligation with every acronym and synonym in it swapped for its counterpart "
            + "(e.g. CDD -> customer due diligence and back), so sections written in the other form are found by BM25 and embedding alike."),
        new(V3RelevanceSelection, "v3 - whole clause, relevance selection, no limits",
            "v2, plus: every paragraph and list item of the clause is searched (no 8-part limit, nothing dropped); every section "
            + "is scored (no 300-candidate limit); a section is selected when it scores clearly above the rest of the documents "
            + "for at least one part of the clause, on keywords or on meaning; the two searches are combined by rank (keyword-only "
            + "matches are no longer dropped) and there is no minimum or maximum number of selected sections."),
        new(V4Passages, "v4 - search passages and equivalent terms",
            "v3, plus: documents are searched as passages of ~150-300 words with their heading path instead of whole sections "
            + "(a long section was only partly visible to the meaning search); a clause part using a term from a group of "
            + "equivalent terms (e.g. timeframe / time period / duration) is also searched once with each alternative; passage "
            + "vectors are loaded once per run and scored in memory (faster)."),
        new(V5GapVerification, "v5 - v4 plus gap double-check",
            "v4, plus: before a gap is saved, its requirement is searched again across every passage of every selected "
            + "document and the AI is asked one short question - does any of these passages cover it? A gap with verified "
            + "covering text is turned into a covered element with that evidence (one small AI call per gap)."),
        new(V6RequirementJudgment, "v6 - v5 plus word roots and institution names (in progress)",
            "v5, plus: keyword search matches word forms (report / reported / reporting) and ignores words found in most "
            + "passages; being extended (Plan V2) with the bank's own name for itself, a saved requirement list per clause, "
            + "evidence per requirement and a per-requirement judgment."),
    ];

    public static bool IsKnown(int version) => All.Any(v => v.Version == version);

    public static string Label(int version) => All.FirstOrDefault(v => v.Version == version)?.Label ?? $"v{version}";
}
