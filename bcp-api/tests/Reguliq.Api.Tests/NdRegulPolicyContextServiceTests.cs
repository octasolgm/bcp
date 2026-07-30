using Reguliq.Api.Models;
using Reguliq.Api.Services.NewDashboard;
using Xunit;

namespace Reguliq.Api.Tests;

public class NdRegulPolicyContextServiceTests
{
    [Fact]
    public void BuildContextForClause_uses_full_manual_when_pages_le_50()
    {
        var bundle = NdRegulPolicyContextService.FromPayloads([
            new InternalDocPayload("h", "Manual.pdf", "annual sanctions review policy text", null),
        ]);

        Assert.True(bundle.TotalPages <= NdRegulPolicyContextService.FullManualMaxPages);
        var ctx = bundle.BuildContextForClause("sanctions review annually");
        Assert.Contains("=== DOCUMENT: Manual.pdf ===", ctx);
        Assert.Contains("annual sanctions review", ctx);
    }

    [Fact]
    public void BuildContextForClause_retrieves_keyword_chunks_when_pages_gt_50()
    {
        var longMarkdown = string.Join(
            "\n",
            Enumerable.Range(1, 60).Select(p =>
                $"<!-- Page {p} -->\nPage {p} content about topic {p % 7}."));
        longMarkdown += "\n<!-- Page 61 -->\nBeneficial ownership threshold is fifty percent for all customers.";

        var bundle = NdRegulPolicyContextService.FromPayloads([
            new InternalDocPayload("h", "BigManual.pdf", longMarkdown, null),
        ]);

        Assert.True(bundle.TotalPages > NdRegulPolicyContextService.FullManualMaxPages);
        var ctx = bundle.BuildContextForClause(
            "beneficial ownership fifty percent threshold requirement");
        Assert.Contains("fifty percent", ctx, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Page 1 content", ctx);
    }
}
