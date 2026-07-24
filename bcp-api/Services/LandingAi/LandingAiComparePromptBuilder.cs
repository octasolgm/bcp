using Reguliq.Api.Models;

namespace Reguliq.Api.Services.LandingAi;

/// <summary>Builds Landing AI compare markdown. V1 = original; V2 = ND v8 multi-doc accuracy prompt.</summary>
public static class LandingAiComparePromptBuilder
{
    public static string Build(
        GovPoint point,
        string internalMarkdown,
        string internalFileName,
        ComparePromptVersion version = ComparePromptVersion.V1)
        => Build(point, [(internalFileName, internalMarkdown)], version);

    public static string Build(
        GovPoint point,
        IReadOnlyList<(string FileName, string Markdown)> internalDocs,
        ComparePromptVersion version = ComparePromptVersion.V1)
    {
        var requirement = FormatRequirement(point);
        var docSection = internalDocs.Count switch
        {
            0 => throw new ArgumentException("At least one internal document is required.", nameof(internalDocs)),
            1 => BuildSingleDocSection(internalDocs[0].FileName, internalDocs[0].Markdown, version),
            _ => BuildMultiDocSection(internalDocs, version),
        };

        var template = version == ComparePromptVersion.V2 ? PromptTemplateV2 : PromptTemplateV1;

        return $"""
{template}

---
INPUT DATA:

{docSection}
REQUIREMENT POINT TO CHECK:

{requirement}
""";
    }

    private static string BuildSingleDocSection(string fileName, string markdown, ComparePromptVersion version)
    {
        var searchHint = version == ComparePromptVersion.V2
            ? "search this entire document before concluding Non-Compliant"
            : "search this entire section";

        return $"""
ATTACHED INTERNAL PROCESS DOCUMENT ({fileName} — parsed markdown from internal policy PDF; {searchHint}):

{markdown}
""";
    }

    private static string BuildMultiDocSection(
        IReadOnlyList<(string FileName, string Markdown)> internalDocs,
        ComparePromptVersion version)
    {
        var sb = new System.Text.StringBuilder();
        var intro = version == ComparePromptVersion.V2
            ? $"ATTACHED INTERNAL PROCESS DOCUMENTS ({internalDocs.Count} PDFs — evaluate compliance across ALL documents; search every document before concluding Non-Compliant; cite evidence from any document with its exact file name):"
            : $"ATTACHED INTERNAL PROCESS DOCUMENTS ({internalDocs.Count} PDFs — evaluate compliance across ALL documents; cite evidence from any document):";

        sb.AppendLine(intro);
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

    private const string PromptTemplateV1 = """
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

    private const string PromptTemplateV2 = """
You are an expert regulatory compliance auditor. Evaluate the ENTIRE requirement point — including every sub-obligation in the clause — against ALL attached internal process documents. Use semantic intent analysis, not keyword matching.

Rules:
- Regulatory framework: infer from REQUIREMENT POINT TO CHECK (law, regulation, guideline, circular, or policy). Do not assume a fixed framework or document name.
- Search EVERY attached internal document before concluding Non-Compliant. Compliant if ANY attached document fully addresses all sub-obligations.
- Compare by regulatory meaning and operational control. Different wording is acceptable when the control is equivalent.
- Do not mark Non-Compliant when detailed internal policy clearly covers the same obligations with different wording.
- Compliant: all sub-obligations operationally covered. Partial Compliant: only some covered. Non-Compliant: no equivalent procedure in any attached document after searching all of them.
- comply_status must be one of: Compliant | Partial Compliant | Non-Compliant
- compliance_confidence_percentage: integer 0-100 aligned with status (Compliant 86-100, Partial Compliant 31-85, Non-Compliant 0-30)
- uae_response_compliance_level: primary evidence citation(s). Use one line per source when evidence spans multiple documents or pages.
  Format each line: [Document Name], Section [header or number], Page [N]: 'verbatim internal quote'
  Page [N] MUST be the 1-based PDF viewer page index. Include section numbers from document headers when present (e.g. Section 7.28).
  Use a multi-sentence quote when needed to show intent is met. If Non-Compliant, output exactly: No corresponding procedure found.
- fulfilled_clauses: one bullet (•) per sub-obligation that IS satisfied. Each bullet MUST include its source ref:
  • [sub-obligation summary] — [Document Name], Section [X], Page [N]: 'verbatim quote'
  Output None if nothing is covered.
- corrective_action_plan: required when Partial or Non-Compliant. Start with Gap(s): then numbered items (1) Missing: [sub-intent not met], Fix: [action]. Empty string when Compliant.
- suggested_responsibility: department or role for corrective action. Empty string when Compliant.
- reference_pdf: exact internal document file name(s) that contain the cited evidence. For multiple documents, comma-separate the file names.
- Return structured JSON matching the provided schema only (no markdown fences)
""";
}
