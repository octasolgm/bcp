using System.Text.Json.Nodes;
using Reguliq.Api.Services.NewDashboard;
using Xunit;

namespace Reguliq.Api.Tests;

/// <summary>
/// The V5 (hybrid pipeline) judgment tool schema adds per-field descriptions so the verbatim-quote rule reaches the
/// model inside a forced tool call. V3/V4 must keep the original schema untouched.
/// </summary>
public class NdRegulLlmSchemasTests
{
    private static readonly string[] Fields =
    [
        "design_status", "operating_status", "overall_status", "confidence", "interpretation",
        "policy_extract", "document_reference", "gap_description", "suggested_action", "gap_direction",
    ];

    [Fact]
    public void Original_judgment_schema_has_no_descriptions()
    {
        var props = (JsonObject)NdRegulLlmSchemas.JudgmentToolSchema()["properties"]!;
        foreach (var field in Fields)
            Assert.False(((JsonObject)props[field]!).ContainsKey("description"), field);
    }

    [Fact]
    public void Hybrid_schema_describes_every_field()
    {
        var props = (JsonObject)NdRegulLlmSchemas.HybridJudgmentToolSchema()["properties"]!;
        foreach (var field in Fields)
        {
            var description = ((JsonObject)props[field]!)["description"]?.GetValue<string>();
            Assert.False(string.IsNullOrWhiteSpace(description), field);
        }
    }

    [Fact]
    public void Hybrid_schema_keeps_same_fields_types_and_required_list_as_original()
    {
        var original = NdRegulLlmSchemas.JudgmentToolSchema();
        var hybrid = NdRegulLlmSchemas.HybridJudgmentToolSchema();

        Assert.Equal(original["required"]!.ToJsonString(), hybrid["required"]!.ToJsonString());
        Assert.Equal(original["additionalProperties"]!.ToJsonString(), hybrid["additionalProperties"]!.ToJsonString());

        var originalProps = (JsonObject)original["properties"]!;
        var hybridProps = (JsonObject)hybrid["properties"]!;
        Assert.Equal(originalProps.Select(p => p.Key), hybridProps.Select(p => p.Key));
        foreach (var field in Fields)
        {
            var o = (JsonObject)originalProps[field]!;
            var h = (JsonObject)hybridProps[field]!;
            Assert.Equal(o["type"]!.ToJsonString(), h["type"]!.ToJsonString());
        }

        // policy_extract must stay an array of strings.
        var extract = (JsonObject)hybridProps["policy_extract"]!;
        Assert.Equal("array", extract["type"]!.GetValue<string>());
        Assert.Equal("string", extract["items"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void Hybrid_policy_extract_description_demands_verbatim_quotes()
    {
        var props = (JsonObject)NdRegulLlmSchemas.HybridJudgmentToolSchema()["properties"]!;
        var text = ((JsonObject)props["policy_extract"]!)["description"]!.GetValue<string>();
        Assert.Contains("VERBATIM", text);
        Assert.Contains("empty array", text);
    }

    [Fact]
    public void Building_the_hybrid_schema_does_not_change_the_original_schema()
    {
        _ = NdRegulLlmSchemas.HybridJudgmentToolSchema();
        var props = (JsonObject)NdRegulLlmSchemas.JudgmentToolSchema()["properties"]!;
        Assert.False(((JsonObject)props["policy_extract"]!).ContainsKey("description"));
    }
}
