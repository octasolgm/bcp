using Reguliq.Api.Services.LandingAi;
using Xunit;

namespace Reguliq.Api.Tests;

public class LandingAiDocumentParseServiceTests
{
    [Fact]
    public void ShiftPageMarkers_AddsOffsetToMarkers()
    {
        var input = $"{PolicyPageResolver.PageMarkerPrefix}1 -->\nPage one\n{PolicyPageResolver.PageMarkerPrefix}2 -->\nPage two";
        var shifted = LandingAiDocumentParseService.ShiftPageMarkers(input, 94);
        Assert.Contains($"{PolicyPageResolver.PageMarkerPrefix}95 -->", shifted);
        Assert.Contains($"{PolicyPageResolver.PageMarkerPrefix}96 -->", shifted);
    }

    [Fact]
    public void IsLandingAiPageLimitError_Detects422Message()
    {
        var ex = new InvalidOperationException(
            "Landing AI parse failed (422): {\"error\":\"PDF must not exceed 100 pages.\"}");
        Assert.True(LandingAiDocumentParseService.IsLandingAiPageLimitError(ex));
    }
}
