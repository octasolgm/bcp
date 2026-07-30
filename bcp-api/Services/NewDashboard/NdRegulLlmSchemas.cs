using System.Text.Json.Nodes;

namespace Reguliq.Api.Services.NewDashboard;

/// <summary>Regul.ai tool input schemas — mirrors <c>backend/llm/schemas.py</c>.</summary>
public static class NdRegulLlmSchemas
{
    public const string JudgmentToolName = "record_judgment";
    public const string ReverseMappingToolName = "record_mapping";
    public const string QualitativeToolName = "record_assessment";

    public static JsonObject JudgmentToolSchema() => new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["design_status"] = new JsonObject { ["type"] = "string" },
            ["operating_status"] = new JsonObject { ["type"] = "string" },
            ["overall_status"] = new JsonObject { ["type"] = "string" },
            ["confidence"] = new JsonObject { ["type"] = "number" },
            ["interpretation"] = new JsonObject { ["type"] = "string" },
            ["policy_extract"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" },
            },
            ["document_reference"] = new JsonObject { ["type"] = "string" },
            ["gap_description"] = new JsonObject { ["type"] = "string" },
            ["suggested_action"] = new JsonObject { ["type"] = "string" },
            ["gap_direction"] = new JsonObject { ["type"] = "string" },
        },
        ["required"] = new JsonArray
        {
            "design_status", "operating_status", "overall_status", "confidence",
            "interpretation", "policy_extract", "document_reference",
            "gap_description", "suggested_action", "gap_direction",
        },
        ["additionalProperties"] = false,
    };

    public static JsonObject JudgmentToolDefinition() => new JsonObject
    {
        ["name"] = JudgmentToolName,
        ["description"] = "Record compliance judgment for one regulatory clause against internal policy excerpts.",
        ["input_schema"] = JudgmentToolSchema(),
    };
}
