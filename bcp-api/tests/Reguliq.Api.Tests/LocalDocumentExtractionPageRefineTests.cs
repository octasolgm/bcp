using System.Text.RegularExpressions;
using Reguliq.Api.Services.LandingAi;
using Reguliq.Api.Services.LocalDocs;
using Xunit;

namespace Reguliq.Api.Tests;

public class LocalDocumentExtractionPageRefineTests
{
    private static IReadOnlyList<LocalPageResult> PagesFromParseMarkdown(string markdown)
    {
        var pattern = Regex.Escape(PolicyPageResolver.PageMarkerPrefix) + @"(\d+)\s*-->";
        var matches = Regex.Matches(markdown, pattern);
        var pages = new List<LocalPageResult>();
        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index + matches[i].Length;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : markdown.Length;
            var pageNum = int.Parse(matches[i].Groups[1].Value);
            pages.Add(new LocalPageResult(pageNum, markdown[start..end].Trim(), PageExtractionMethod.Native));
        }

        return pages;
    }

    [Fact]
    public void Split_uses_parse_page_markers_for_multi_page_section()
    {
        var markdown = string.Join('\n', new[]
        {
            $"{PolicyPageResolver.PageMarkerPrefix}13 -->",
            "6. Roles and Responsibilities",
            "Intro paragraph at end of page thirteen.",
            $"{PolicyPageResolver.PageMarkerPrefix}14 -->",
            "The employee who reports an STR will not be held liable whether the suspicion is proven true or not, as long as the report has been made and sent in good faith.",
            $"{PolicyPageResolver.PageMarkerPrefix}15 -->",
            "7. Next chapter",
        });

        var sections = LocalSectionSplitter.Split(PagesFromParseMarkdown(markdown));
        var section6 = sections.FirstOrDefault(s => s.ClauseNo == "6");
        Assert.NotNull(section6);
        Assert.Equal(13, section6!.SourcePage);
        Assert.Equal(14, section6.SourcePageEnd);
        Assert.NotNull(section6.PageBlocks);
        Assert.Equal(2, section6.PageBlocks!.Count);
        Assert.Equal(13, section6.PageBlocks[0].Page);
        Assert.Equal(14, section6.PageBlocks[1].Page);
    }

    [Fact]
    public void Split_does_not_inflate_page_span_by_fuzzy_rematch()
    {
        var markdown = string.Join('\n', new[]
        {
            $"{PolicyPageResolver.PageMarkerPrefix}13 -->",
            "6. Roles and Responsibilities",
            "DIFC requires the Executive Management, and Employees to abide by the rules set forth in this Manual.",
            $"{PolicyPageResolver.PageMarkerPrefix}14 -->",
            "The employee who reports an STR will not be held liable whether the suspicion is proven true or not.",
            $"{PolicyPageResolver.PageMarkerPrefix}18 -->",
            "7. Next chapter starts here.",
        });

        var sections = LocalSectionSplitter.Split(PagesFromParseMarkdown(markdown));
        var section6 = sections.First(s => s.ClauseNo == "6");
        Assert.Equal(13, section6.SourcePage);
        Assert.Equal(14, section6.SourcePageEnd);
        Assert.DoesNotContain(section6.PageBlocks ?? [], b => b.Page >= 18);
    }
}
