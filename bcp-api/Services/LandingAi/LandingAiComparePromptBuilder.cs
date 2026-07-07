using Reguliq.Api.Models;

namespace Reguliq.Api.Services.LandingAi;

/// <summary>Builds Landing AI compare markdown (v2 auditor prompt).</summary>
public static class LandingAiComparePromptBuilder
{
    public static string Build(GovPoint point, string internalMarkdown, string internalFileName)
    {
        var requirement = FormatRequirement(point);
        return $"""
{PromptTemplate}

---
INPUT DATA:

ATTACHED INTERNAL PROCESS DOCUMENT ({internalFileName} — parsed markdown from internal policy PDF; search this entire section):

{internalMarkdown}

REQUIREMENT POINT TO CHECK:

{requirement}
""";
    }

    private static string FormatRequirement(GovPoint point)
    {
        var head = string.Join(' ', new[] { point.PointId, point.Title }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return string.IsNullOrWhiteSpace(head) ? point.Text : $"{head}\n\n{point.Text}";
    }

    private const string PromptTemplate = """
You are an expert automated regulatory compliance auditor specializing in CBUAE and TFS frameworks. Evaluate the ENTIRE requirement point against the internal process document using semantic intent analysis (not keyword matching).

Rules:
- Compare by regulatory meaning and operational effect.
- cite evidence as: Page [X], Section [Y]: 'verbatim internal quote'
- If Non-Compliant, uae_response_compliance_level must be exactly: No corresponding procedure found.
- comply_status must be one of: Compliant | Partial Compliant | Non-Compliant
- compliance_confidence_percentage: integer 0-100 aligned with status
- Return structured JSON matching the provided schema only (no markdown fences)
""";
}
