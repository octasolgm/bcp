using Reguliq.Api.Data.Entities;
using Reguliq.Api.Services.LocalDocs;
using Reguliq.Api.Services.NewDashboard;
using Xunit;

namespace Reguliq.Api.Tests;

public class NdRegulPipelineV4Tests
{
    private static string Words(string prefix, int count) =>
        string.Join(' ', Enumerable.Range(1, count).Select(i => $"{prefix}{i}")) + ".";

    private static LocalSection Section(string no, params string[] lines) =>
        new(no, string.Join('\n', lines), 10);

    [Fact]
    public void A_long_section_becomes_passages_that_fit_the_embedding_window_and_lose_nothing()
    {
        // An annex of typologies: one numbered section of ~1,500 words with "B.n" sub-headings inside.
        var lines = new List<string> { "Annex 1 Red Flag Indicators" };
        for (var b = 1; b <= 18; b++)
        {
            lines.Add($"B.{b} Typology number {b}");
            lines.Add("How it works: " + Words($"w{b}x", 40));
            lines.Add("Possible indicators");
            lines.Add("o " + Words($"i{b}y", 35));
        }

        var section = Section("Annex 1", lines.ToArray());
        var passages = LocalPassageSplitter.Split("AML Manual", [section]);

        Assert.True(passages.Count > 5);
        Assert.All(passages, p => Assert.True(LocalPassageSplitter.WordCount(p.Text) <= LocalPassageSplitter.MaxWords, p.Text));
        var joined = string.Join("\n", passages.Select(p => p.Text));
        Assert.All(lines, l => Assert.Contains(l, joined));
        Assert.All(passages, p => Assert.Equal("Annex 1", p.ClauseNo));
    }

    [Fact]
    public void A_numbered_sub_heading_starts_a_new_passage_and_names_its_text()
    {
        var section = Section("Annex 1",
            "Annex 1 Red Flag Indicators",
            "B.17 Cash couriers",
            "How it works: " + Words("c", 30),
            "B.18 Other payment technologies",
            "How it works: Utilizing emerging payment technologies such as virtual currencies/cryptocurrencies.",
            "Possible indicators",
            "o Unjustified transactions to and from Cryptocurrency platforms and digital assets exchanges.");

        var passages = LocalPassageSplitter.Split("AML Manual", [section]);

        var crypto = Assert.Single(passages, p => p.Text.Contains("virtual currencies/cryptocurrencies", StringComparison.Ordinal));
        Assert.Contains("B.18 Other payment technologies", crypto.HeadingPath);
        Assert.DoesNotContain("B.17", crypto.HeadingPath);
        Assert.StartsWith("AML Manual > Annex 1 Red Flag Indicators", crypto.HeadingPath);
    }

    [Fact]
    public void Heading_path_includes_the_parent_section_heading()
    {
        var parent = Section("7", "7. Implementation", Words("p", 20));
        var child = Section("7.5", "7.5 Customer Risk Assessment", "Risk factors include involvement in virtual assets.", Words("q", 30));

        var passages = LocalPassageSplitter.Split("AML Manual", [parent, child]);

        var p = Assert.Single(passages, x => x.SectionIndex == 1);
        Assert.Equal("AML Manual > 7. Implementation > 7.5 Customer Risk Assessment", p.HeadingPath);
    }

    [Fact]
    public void Passages_carry_the_page_they_start_on()
    {
        var section = new LocalSection(
            "17.2",
            "",
            21,
            24,
            [
                new LocalSectionPageBlock(21, "17.2 Best Practices for Drafting an STR\n" + Words("a", 150)),
                new LocalSectionPageBlock(22, Words("b", 150) + "\nIf the activity takes place over a period of time, describe the duration of the activity. " + Words("d", 60)),
            ]);

        var passages = LocalPassageSplitter.Split("Implementation Manual", [section]);

        var duration = Assert.Single(passages, p => p.Text.Contains("describe the duration of the activity", StringComparison.Ordinal));
        Assert.Equal(22, duration.SourcePage);
    }

    [Fact]
    public void A_term_with_several_equivalents_is_searched_once_with_each()
    {
        static DictionaryExpansionService.ExpansionMatch Syn(string a, string b) => new(Guid.NewGuid(), a, b, a, b);
        var expanded = new DictionaryExpansionService.QueryExpansionResult(
            [new DictionaryExpansionService.ExpansionMatch(Guid.NewGuid(), "STR", "suspicious transaction report", "STR", "suspicious transaction report")],
            [Syn("timeframe", "time period"), Syn("timeframe", "period of time"), Syn("timeframe", "duration")]);

        var variants = RegulEmbeddingRetrievalService.BuildExpandedWordingVariants(
            "The timeframe of the transaction is irrelevant to an STR.", expanded);

        Assert.Equal(
            [
                "The time period of the transaction is irrelevant to an suspicious transaction report.",
                "The period of time of the transaction is irrelevant to an suspicious transaction report.",
                "The duration of the transaction is irrelevant to an suspicious transaction report.",
            ],
            variants);
    }

    [Fact]
    public void No_dictionary_match_gives_no_variants()
    {
        Assert.Empty(RegulEmbeddingRetrievalService.BuildExpandedWordingVariants(
            "Nothing to expand here.", new DictionaryExpansionService.QueryExpansionResult([], [])));
    }

    [Fact]
    public void Passage_context_text_puts_the_heading_path_first_without_square_brackets()
    {
        Assert.Equal(
            "Heading: AML Manual > 6. Roles\nThe employee who reports an STR will not be held liable.",
            RegulEmbeddingRetrievalService.PassageContextText("AML Manual > 6. Roles", "The employee who reports an STR will not be held liable."));
        Assert.Equal("text", RegulEmbeddingRetrievalService.PassageContextText("", "text"));
    }

    [Fact]
    public void Each_document_is_searched_with_one_extraction_azure_first_then_the_latest()
    {
        var docA = Guid.NewGuid();
        var docB = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        NdLocalDocumentExtraction E(Guid doc, string engine, int minutesAgo) =>
            new() { Id = Guid.NewGuid(), StoredDocumentId = doc, Engine = engine, IndexedAt = now.AddMinutes(-minutesAgo) };
        var azureOld = E(docA, OcrEngineNames.AzureDocIntelligence, 60);
        var tesseractNew = E(docA, OcrEngineNames.Tesseract, 1);
        var rapidOld = E(docB, OcrEngineNames.RapidOcr, 30);
        var doclingNew = E(docB, OcrEngineNames.DoclingLight, 5);

        var picked = NdPassageIndexService.PickSearchExtractions([azureOld, tesseractNew, rapidOld, doclingNew]);

        Assert.Equal(2, picked.Count);
        Assert.Contains(azureOld, picked);
        Assert.Contains(doclingNew, picked);
    }

    [Theory]
    [InlineData("Submit a SAR within a reasonable timeframe of identifying the suspicious activity;", "reasonable timeframe of identifying", true)]
    [InlineData("If the activity takes place over a period\nof time, describe the duration of the\nactivity.", "describe the duration of the activity", true)]
    [InlineData("expanding the time period for reviewing alerted transactions (e.g., from 30 days to 90 days)", "from 30 days to 90 days", true)]
    [InlineData("The employee who reports an STR will not be held liable", "employee who reports an STR will not be held liable", true)]
    [InlineData("Unjustified transactions to and from Cryptocurrency platforms", "virtual assets", false)]
    public void Retrieval_check_snippets_match_despite_line_breaks_and_case(string unitText, string snippet, bool expected) =>
        Assert.Equal(expected, RegulEmbeddingRetrievalService.SnippetMatches(unitText, snippet));
}
