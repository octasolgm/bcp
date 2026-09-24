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

    /// <summary>V5 (hybrid pipeline) only: same fields as <see cref="JudgmentToolSchema"/>, plus a description on each
    /// field. In a forced tool call the model only sees the schema next to the field it is filling, so without
    /// these the verbatim-quote rule that lives in the long system prompt was being skipped (empty policy_extract).</summary>
    public static JsonObject HybridJudgmentToolSchema()
    {
        var schema = JudgmentToolSchema();
        var props = (JsonObject)schema["properties"]!;
        void Describe(string field, string text) => ((JsonObject)props[field]!)["description"] = text;
        Describe("design_status", "Design adequacy of the policy for this clause: compliant, partial or non_compliant.");
        Describe("operating_status", "Operating effectiveness evidenced by the excerpts: compliant, partial or non_compliant.");
        Describe("overall_status", "Final verdict for this clause: compliant, partial or non_compliant.");
        Describe("confidence", "Confidence in the verdict, a number between 0 and 1.");
        Describe("interpretation", "Short reasoning that maps each requirement in the clause to the evidence found (or missing).");
        Describe(
            "policy_extract",
            "REQUIRED evidence: quotes copied VERBATIM, character for character, from the internal policy excerpts provided " +
            "(keep OCR artifacts, do not paraphrase). Give one array item per supporting passage, and include every passage " +
            "that supports the verdict. Only return an empty array if no excerpt is relevant at all.");
        Describe(
            "document_reference",
            "Document name and section/page taken from the exact [bracket label] of the excerpts you quoted in policy_extract. Never invent a page or section.");
        Describe("gap_description", "What is missing versus the clause. REQUIRED when overall_status is partial or non_compliant. Must be \"N/A\" when overall_status is compliant: a compliant clause has no gap, so do not list minor observations here.");
        Describe("suggested_action", "Concrete corrective action that closes the gap. REQUIRED whenever gap_description is not N/A (partial or non_compliant). Must be \"N/A\" when overall_status is compliant.");
        Describe("gap_direction", "Kind of gap, or an empty string when there is none.");
        return schema;
    }

    public static JsonObject JudgmentToolDefinition() => new JsonObject
    {
        ["name"] = JudgmentToolName,
        ["description"] = "Record compliance judgment for one regulatory clause against internal policy excerpts.",
        ["input_schema"] = JudgmentToolSchema(),
    };
}
