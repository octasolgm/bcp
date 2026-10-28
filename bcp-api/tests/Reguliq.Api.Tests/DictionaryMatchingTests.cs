using Reguliq.Api.Services.LocalDocs;
using Reguliq.Api.Services.NewDashboard;
using Xunit;

namespace Reguliq.Api.Tests;

public class DictionaryMatchingTests
{
    [Theory]
    [InlineData("The policy applies to all staff.", "policy", true)]
    [InlineData("Policyholders must be screened.", "policy", false)]
    [InlineData("Customer Due Diligence must be applied.", "customer due diligence", true)]
    [InlineData("customer due diligences", "customer due diligence", false)]
    [InlineData("(risk assessment)", "risk assessment", true)]
    [InlineData("risk assessment-based approach", "risk assessment", true)]
    public void Full_forms_and_synonyms_match_whole_words_only(string text, string phrase, bool expected) =>
        Assert.Equal(expected, DictionaryExpansionService.ContainsWholePhrase(text, phrase));

    [Fact]
    public void Expanded_wording_never_rewrites_inside_a_longer_word()
    {
        var expanded = new DictionaryExpansionService.QueryExpansionResult(
            [],
            [new DictionaryExpansionService.ExpansionMatch(Guid.NewGuid(), "policy", "procedure", "policy", "procedure")]);
        Assert.Equal(
            "Policyholders are covered by the procedure.",
            RegulEmbeddingRetrievalService.BuildExpandedWording("Policyholders are covered by the policy.", expanded));
    }

    [Fact]
    public void Retired_seed_synonyms_are_no_longer_in_the_seed_file()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "SeedData", "synonym-seed.json");
        var json = File.ReadAllText(path);
        foreach (var (termA, termB) in DictionaryExpansionService.RetiredSeedSynonyms)
            Assert.DoesNotContain($"\"termA\": \"{termA}\", \"termB\": \"{termB}\"", json);
    }
}
