using Reguliq.Api.Services.LocalDocs;
using Reguliq.Api.Services.NewDashboard;
using Xunit;

namespace Reguliq.Api.Tests;

public class NdRegulPipelineV2Tests
{
    private static DictionaryExpansionService.ExpansionMatch Acronym(string acronym, string definition, bool matchedAcronym) =>
        matchedAcronym
            ? new(Guid.NewGuid(), acronym, definition, acronym, definition)
            : new(Guid.NewGuid(), acronym, definition, definition, acronym);

    private static DictionaryExpansionService.ExpansionMatch Synonym(string a, string b) => new(Guid.NewGuid(), a, b, a, b);

    private static DictionaryExpansionService.QueryExpansionResult Result(
        DictionaryExpansionService.ExpansionMatch[] acronyms, DictionaryExpansionService.ExpansionMatch[]? synonyms = null) =>
        new(acronyms, synonyms ?? []);

    [Fact]
    public void Acronym_in_the_clause_is_swapped_for_its_full_form()
    {
        var text = "FIs must apply CDD before opening an account.";
        var reworded = RegulEmbeddingRetrievalService.BuildExpandedWording(
            text, Result([Acronym("CDD", "customer due diligence", matchedAcronym: true)]));
        Assert.Equal("FIs must apply customer due diligence before opening an account.", reworded);
    }

    [Fact]
    public void Full_form_in_the_clause_is_swapped_for_the_acronym_and_both_directions_do_not_chain()
    {
        var text = "Report Money Laundering suspicions; ML includes self-laundering.";
        var reworded = RegulEmbeddingRetrievalService.BuildExpandedWording(
            text,
            Result([
                Acronym("ML", "money laundering", matchedAcronym: true),
                Acronym("ML", "money laundering", matchedAcronym: false),
            ]));
        Assert.Equal("Report ML suspicions; money laundering includes self-laundering.", reworded);
    }

    [Fact]
    public void Acronym_only_matches_whole_words_case_sensitively()
    {
        var text = "The HTML report and the STR filing.";
        var reworded = RegulEmbeddingRetrievalService.BuildExpandedWording(
            text, Result([Acronym("STR", "suspicious transaction report", matchedAcronym: true)]));
        Assert.Equal("The HTML report and the suspicious transaction report filing.", reworded);
    }

    [Fact]
    public void Synonyms_are_swapped_and_no_match_gives_null()
    {
        Assert.Equal(
            "Staff must escalate to the compliance officer.",
            RegulEmbeddingRetrievalService.BuildExpandedWording(
                "Staff must escalate to the MLRO.", Result([], [Synonym("MLRO", "compliance officer")])));
        Assert.Null(RegulEmbeddingRetrievalService.BuildExpandedWording("No terms here.", Result([])));
    }

    [Fact]
    public void Pipeline_versions_are_registered_and_v2_is_the_default()
    {
        Assert.True(NdRegulPipelineVersions.IsKnown(1));
        Assert.True(NdRegulPipelineVersions.IsKnown(2));
        Assert.Equal(2, NdRegulPipelineVersions.Default);
        Assert.Equal(1, NdAnalysisEvalService.PipelineVersionOf("{\"subObligations\":[]}"));
        Assert.Equal(2, NdAnalysisEvalService.PipelineVersionOf("{\"pipelineVersion\":2}"));
        Assert.Null(NdAnalysisEvalService.PipelineVersionOf(null));
    }
}
