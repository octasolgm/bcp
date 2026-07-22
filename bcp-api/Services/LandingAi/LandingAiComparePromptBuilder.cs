using Reguliq.Api.Models;

namespace Reguliq.Api.Services.LandingAi;

/// <summary>Builds Landing AI compare markdown (v2 auditor prompt).</summary>
public static class LandingAiComparePromptBuilder
{
    public static string Build(GovPoint point, string internalMarkdown, string internalFileName)
        => Build(point, [(internalFileName, internalMarkdown)]);

    public static string Build(GovPoint point, IReadOnlyList<(string FileName, string Markdown)> internalDocs)
    {
        var requirement = FormatRequirement(point);
        var docSection = internalDocs.Count switch
        {
            0 => throw new ArgumentException("At least one internal document is required.", nameof(internalDocs)),
            1 => $"""
ATTACHED INTERNAL PROCESS DOCUMENT ({internalDocs[0].FileName} — parsed markdown from internal policy PDF; search this entire section):

{internalDocs[0].Markdown}
""",
            _ => BuildMultiDocSection(internalDocs),
        };

        return $"""
{PromptTemplate}

---
INPUT DATA:

{docSection}
REQUIREMENT POINT TO CHECK:

{requirement}
""";
    }

    private static string BuildMultiDocSection(IReadOnlyList<(string FileName, string Markdown)> internalDocs)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(
            $"ATTACHED INTERNAL PROCESS DOCUMENTS ({internalDocs.Count} PDFs — evaluate compliance across ALL documents; cite evidence from any document):");
        for (var i = 0; i < internalDocs.Count; i++)
        {
            var (name, md) = internalDocs[i];
            sb.AppendLine();
            sb.AppendLine($"--- DOCUMENT {i + 1}: {name} (parsed markdown) ---");
            sb.AppendLine();
            sb.AppendLine(md);
        }
        return sb.ToString();
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
- Page [X] MUST be the 1-based PDF file page index as shown in a PDF viewer (scroll bar / page counter), NOT printed footer numbers or table-of-contents page numbers.
- If Non-Compliant, uae_response_compliance_level must be exactly: No corresponding procedure found.
- comply_status must be one of: Compliant | Partial Compliant | Non-Compliant
- compliance_confidence_percentage: integer 0-100 aligned with status
- Return structured JSON matching the provided schema only (no markdown fences)
""";
}
