using Reguliq.Api.Models;
using Reguliq.Api.Services;
using Xunit;

namespace Reguliq.Api.Tests;

public class DualVerifyAgreementServiceTests
{
    private const string LandingCompliant = """
        Reference PDF: I M P T F S.pdf
        Comply Yes/No (Status): Compliant
        Compliance Confidence %: 92
        Output/Response: Policy addresses the requirement.
        """;

    private const string LlmCompliant = """
        Reference PDF: I M P T F S.pdf
        Comply Yes/No (Status): Compliant
        Compliance Confidence %: 90
        Output/Response: Internal policy is aligned.
        """;

    [Fact]
    public void Compare_AlignedStatuses_ReturnsAligned()
    {
        var result = DualVerifyAgreementService.Compare(LandingCompliant, LlmCompliant);
        Assert.Equal("aligned", result.Status);
        Assert.Equal("Compliant", result.LandingStatus);
        Assert.Equal("Compliant", result.LlmStatus);
    }

    [Fact]
    public void Compare_StatusMismatch_ReturnsStatusMismatch()
    {
        var llm = LandingCompliant.Replace("Compliant", "Non-Compliant");
        var result = DualVerifyAgreementService.Compare(LandingCompliant, llm);
        Assert.Equal("status_mismatch", result.Status);
    }

    [Fact]
    public void Compare_LargeConfidenceGap_ReturnsConfidenceGap()
    {
        var llm = LandingCompliant.Replace("90", "40");
        var result = DualVerifyAgreementService.Compare(LandingCompliant, llm);
        Assert.Equal("confidence_gap", result.Status);
    }

    [Fact]
    public void ToJson_FromJson_RoundTrips()
    {
        var dto = DualVerifyAgreementService.Compare(LandingCompliant, LlmCompliant);
        var json = DualVerifyAgreementService.ToJson(dto);
        var restored = DualVerifyAgreementService.FromJson(json);
        Assert.NotNull(restored);
        Assert.Equal(dto.Status, restored!.Status);
        Assert.Equal(dto.LandingConfidence, restored.LandingConfidence);
    }
}
