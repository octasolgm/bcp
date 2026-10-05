using System.Text.Json;
using Reguliq.Api.Services.LocalDocs;
using Xunit;

namespace Reguliq.Api.Tests;

public class AzureDocumentIntelligencePageBuilderTests
{
    [Fact]
    public void BuildPerPageContent_uses_nested_pages_lines_spans()
    {
        const string content = "AAA page one BBB page two CCC";
        var analyze = JsonDocument.Parse(
            """
            {
              "pages": [
                {
                  "pageNumber": 1,
                  "lines": [
                    { "content": "AAA page one", "spans": [ { "offset": 0, "length": 12 } ] }
                  ]
                },
                {
                  "pageNumber": 2,
                  "lines": [
                    { "content": "BBB page two CCC", "spans": [ { "offset": 13, "length": 16 } ] }
                  ]
                }
              ]
            }
            """);

        var pages = AzureDocumentIntelligencePageBuilder.BuildPerPageContent(analyze.RootElement, content, 2);

        Assert.NotNull(pages);
        Assert.Equal(2, pages!.Count);
        Assert.Contains("page one", pages[0]);
        Assert.Contains("page two", pages[1]);
    }

    [Fact]
    public void SplitMarkdownOnPageBoundaries_splits_page_number_comments()
    {
        const string md = "intro<!-- PageNumber=\"2\" -->body";
        var parts = AzureDocumentIntelligencePageBuilder.SplitMarkdownOnPageBoundaries(md);
        Assert.Equal(2, parts.Count);
        Assert.Contains("intro", parts[0]);
        Assert.Contains("body", parts[1]);
    }
}
