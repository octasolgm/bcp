using Reguliq.Api.Services.LocalDocs;
using Xunit;

namespace Reguliq.Api.Tests;

public class LocalSectionSplitterTests
{
    private static IReadOnlyList<LocalSection> Split(string text) =>
        LocalSectionSplitter.Split([new LocalPageResult(1, text, PageExtractionMethod.Native)]);

    [Fact]
    public void Wrapped_article_reference_at_line_start_does_not_split_the_clause()
    {
        // Real case (CBUAE AML-CFT guidance, clause 3.1): the PDF wraps "AML-CFT Decision" / "Article 4.1);"
        // so a line starts with "Article 4.1);" — it must stay inside clause 3.1, not open a fake clause.
        var text = string.Join('\n',
            "3.1 Summary of Minimum Statutory Obligations of Supervised Institutions",
            "The AML-CFT Law and the AML-CFT Decision set out the minimum statutory obligations as follows:",
            "· To identify, assess, understand risks (AML-CFT Law Article 16.1(a), AML-CFT Decision",
            "Article 4.1);",
            "· To define the scope of and take necessary due diligence measures (AML-CFT Law Article",
            "16.1(b), AML-CFT Decision Article 4.1(a) and 2);",
            "· To appoint a compliance officer (AML-CFT Decision Article 21,",
            "44.12);",
            "· To maintain adequate records (AML-CFT Law Article 16.1(f), AML-CFT Decision Article",
            "7.2, 24).",
            "3.2 Confidentiality and Data Protection",
            "Financial Institutions are obliged to report.");

        var sections = Split(text);

        Assert.Equal(["3.1", "3.2"], sections.Select(s => s.ClauseNo).ToArray());
        var first = sections[0].ClauseText;
        Assert.Contains("To maintain adequate records", first);
        Assert.Contains("Article 4.1);", first);
    }

    [Theory]
    [InlineData("Article 4.1);")]
    [InlineData("Article 4.2(a));")]
    [InlineData("Article 21, 44.12);")]
    [InlineData("Article 60);")]
    [InlineData("Article 7.2, 24).")]
    [InlineData("Article 44.11 of the Cabinet Decision No. (10) of 2019")]
    [InlineData("Section 6.2 above and")]
    public void Reference_shaped_lines_are_not_headings(string line)
    {
        var sections = Split($"1. Scope\nSome opening text that runs on and on\n{line}\nmore text after it.");

        Assert.Single(sections);
        Assert.Equal("1", sections[0].ClauseNo);
        Assert.Contains(line, sections[0].ClauseText);
    }

    [Theory]
    [InlineData("Article 12", "Article 12")]
    [InlineData("Article 12 Customer Due Diligence", "Article 12")]
    [InlineData("Article 12: Customer Due Diligence", "Article 12")]
    [InlineData("Article 12 - Customer Due Diligence", "Article 12")]
    [InlineData("Section 6.2 Reporting Duties", "Section 6.2")]
    [InlineData("Rule 9.4.1 Independent Audit", "Rule 9.4.1")]
    [InlineData("Chapter 3 Governance", "Chapter 3")]
    public void Real_labelled_headings_still_split(string heading, string expectedNo)
    {
        var sections = Split($"Opening paragraph before any heading.\n{heading}\nBody text of the clause.");

        Assert.Contains(sections, s => s.ClauseNo == expectedNo);
    }

    [Fact]
    public void Annex_heading_still_namespaces_its_own_numbering()
    {
        var sections = Split(string.Join('\n',
            "2. Sanctions Compliance Program",
            "Body of the real clause two.",
            "Annex 1 Red Flag Indicators",
            "2. Red Flag Indicators for PF",
            "Body of the annex sub list."));

        Assert.Equal(["2", "Annex 1", "Annex 1.2"], sections.Select(s => s.ClauseNo).ToArray());
    }

    [Fact]
    public void Numbered_headings_and_footnotes_behave_as_before()
    {
        var sections = Split(string.Join('\n',
            "1.1 Purpose of this guidance",
            "Text of one point one.",
            "3 Website: Home | Committee for the Sanctions",
            "still part of 1.1 after the footnote.",
            "1.2 Definitions",
            "Text of one point two."));

        Assert.Equal(["1.1", "1.2"], sections.Select(s => s.ClauseNo).ToArray());
        Assert.Contains("still part of 1.1", sections[0].ClauseText);
    }

    [Fact]
    public void Table_of_contents_entries_are_dropped_when_the_real_clause_exists()
    {
        // A plain-text contents page: "3.1 Title" followed only by its page number. The real clauses come
        // later. Only the real ones may survive, one per number.
        var contents = string.Join('\n',
            "1.1 Purpose and Scope", "5",
            "3.1 Summary of Minimum Statutory Obligations", "13",
            "3.2 Confidentiality and Data Protection", "14");
        var body = string.Join('\n',
            "1.1 Purpose and Scope", "The purpose of this guidance is to help institutions comply.",
            "3.1 Summary of Minimum Statutory Obligations", "The law sets out minimum obligations.",
            "3.2 Confidentiality and Data Protection", "Institutions must protect data.");

        var sections = LocalSectionSplitter.Split([
            new LocalPageResult(2, contents, PageExtractionMethod.Native),
            new LocalPageResult(13, body, PageExtractionMethod.Native),
        ]);

        Assert.Equal(["1.1", "3.1", "3.2"], sections.Select(s => s.ClauseNo).ToArray());
        Assert.All(sections, s => Assert.True(s.ClauseText.Length > 50));
    }

    private const string RunningHeader = "Anti-Money Laundering Guidelines for Financial Institutions";

    [Fact]
    public void A_whole_contents_page_is_skipped_including_its_last_entry_and_what_follows_it()
    {
        // The last contents entry ("3.11 ... 23") is followed by a part title, a bare page number and the
        // next chapter's contents line — none of which is clause text, and none may leak into a clause.
        var contents = string.Join('\n',
            "1.1 Purpose and Scope", "5",
            "3.9 The ML Phases", "19",
            "3.10 ML/FT Typologies", "20",
            "3.11 Sanctions against Persons Violating Reporting Obligations", "23",
            "Part II-Identification and Assessment of ML/FT Risks", "25",
            "4. Identification and Assessment of ML/FT Risks", "25");
        var body = string.Join('\n',
            "1.1 Purpose and Scope", "The purpose of this guidance is to help institutions comply.",
            "3.11 Sanctions against Persons Violating Reporting Obligations", "Penalties apply to persons who fail to report.");

        var sections = LocalSectionSplitter.Split([
            new LocalPageResult(2, contents, PageExtractionMethod.Native),
            new LocalPageResult(20, body, PageExtractionMethod.Native),
        ]);

        Assert.Equal(["1.1", "3.11"], sections.Select(s => s.ClauseNo).ToArray());
        Assert.DoesNotContain(sections, s => s.ClauseText.Contains("Part II-Identification"));
    }

    [Fact]
    public void Contents_entry_followed_only_by_a_running_page_header_is_dropped_as_a_duplicate()
    {
        // A short contents spill-over page (too few entries to be recognised as a contents page): the entry
        // is followed by the running header, which repeats on every page and is not clause text.
        var spill = string.Join('\n', "8.3 Group Oversight", "90", RunningHeader, RunningHeader);
        var body = string.Join('\n',
            "8.3 Group Oversight", "Institutions must apply group-wide programmes.", RunningHeader);

        var sections = LocalSectionSplitter.Split([
            new LocalPageResult(4, spill, PageExtractionMethod.Native),
            new LocalPageResult(30, "9.1 Another Clause\nSome other clause text.\n" + RunningHeader, PageExtractionMethod.Native),
            new LocalPageResult(91, body, PageExtractionMethod.Native),
        ]);

        var clause = Assert.Single(sections, s => s.ClauseNo == "8.3");
        Assert.Contains("group-wide programmes", clause.ClauseText);
    }

    [Fact]
    public void Page_footnote_that_repeats_a_top_level_number_is_folded_back_into_the_open_clause()
    {
        // Real case: the footnote "1 Social Media ..." sits in the middle of clause 3.8's sentence
        // ("... should pay" / footnote / "special attention ..."). It must not become a clause "1", and the
        // end of 3.8's sentence must stay in 3.8.
        var text = string.Join('\n',
            "1. Introduction",
            "1.1 Purpose and Scope", "The purpose of this guidance.",
            "3.8 Financing of Illegal Organisations",
            "When assessing their risk exposure, FIs should pay",
            "1 Social Media and Terrorism Financing: A joint project by Asia/Pacific Group on Money Laundering",
            "Financial Action Task Force, APG/MENAFATF, January 2019, p.4.",
            "special attention to the regulatory disclosure requirements of organisations.",
            "3.9 The ML Phases", "Money laundering has three phases.");

        var sections = Split(text);

        Assert.Equal(["1", "1.1", "3.8", "3.9"], sections.Select(s => s.ClauseNo).ToArray());
        var clause38 = sections.Single(s => s.ClauseNo == "3.8").ClauseText;
        Assert.Contains("special attention to the regulatory disclosure", clause38);
        Assert.Contains("Social Media and Terrorism Financing", clause38);
    }

    [Fact]
    public void A_restarted_chapter_that_has_its_own_sub_clauses_is_not_merged()
    {
        // Part II restarts at "1" and is followed by "1.1", so it is a real chapter, not a footnote.
        var text = string.Join('\n',
            "1. Alpha", "1.1 Alpha One", "Alpha one text.",
            "2. Beta", "2.1 Beta One", "Beta one text.",
            "1. Gamma", "1.1 Gamma One", "Gamma one text.");

        var sections = Split(text);

        Assert.Equal(2, sections.Count(s => s.ClauseNo == "1"));
        Assert.Equal(2, sections.Count(s => s.ClauseNo == "1.1"));
    }

    [Fact]
    public void Numbered_list_inside_a_clause_stays_in_that_clause_once_chapter_numbers_are_used()
    {
        var text = string.Join('\n',
            "1. Scope", "1.1 Purpose", "Purpose text.",
            "2. Overview", "Overview text.",
            "3. Roles", "Roles text.",
            "4. Reporting",
            "Institutions must:",
            "1. Report suspicious activity to the FIU.",
            "2. Keep records of every report.",
            "3. Cooperate with the authorities.",
            "5. Review", "Review text.");

        var sections = Split(text);

        Assert.Equal(["1", "1.1", "2", "3", "4", "5"], sections.Select(s => s.ClauseNo).ToArray());
        var reporting = sections.Single(s => s.ClauseNo == "4").ClauseText;
        Assert.Contains("Keep records of every report", reporting);
        Assert.Contains("Cooperate with the authorities", reporting);
    }

    [Fact]
    public void Heading_only_clause_is_kept_when_no_real_clause_shares_its_number()
    {
        var sections = Split(string.Join('\n',
            "4. Risk Management", "25",
            "5. Reporting", "Institutions must report."));

        Assert.Equal(["4", "5"], sections.Select(s => s.ClauseNo).ToArray());
    }
}
