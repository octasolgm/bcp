using Reguliq.Api.Models;

namespace Reguliq.Api.Services.LandingAi;

/// <summary>Builds Landing AI compare markdown. V1 = original; V2 = ND v8; V3 = Regul.ai judgment (analyse-v9).</summary>
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

        var template = version switch
        {
            ComparePromptVersion.V3 => PromptTemplateV3,
            ComparePromptVersion.V2 => PromptTemplateV2,
            _ => PromptTemplateV1,
        };

        return $"""
{template}

---
INPUT DATA:

{docSection}
REQUIREMENT POINT TO CHECK:

{requirement}
""";
    }

  /// <summary>Read-only V3 compare template for admin prompt review.</summary>
    public static string GetPromptTemplateV3() => PromptTemplateV3;

    private static bool UsesEnhancedSearchHints(ComparePromptVersion version) =>
        version is ComparePromptVersion.V2 or ComparePromptVersion.V3;

    private static string BuildSingleDocSection(string fileName, string markdown, ComparePromptVersion version)
    {
        var searchHint = UsesEnhancedSearchHints(version)
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
        var intro = UsesEnhancedSearchHints(version)
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

    /// <summary>
    /// Regul.ai JUDGMENT_SYSTEM_PROMPT adapted to BCP Landing AI JSON schema fields.
    /// Source: Regul.ai/app/backend/llm/prompts.py JUDGMENT_SYSTEM_PROMPT
    /// </summary>
    private const string PromptTemplateV3 = """
You are a compliance analyst comparing a single regulatory requirement clause against a bank's internal policy documents. Judge whether the internal policy documents cover the requirement (design). Documents alone cannot prove operating effectiveness.

Document-perspective rule — judge the internal manual as a bank IMPLEMENTING the regulator's requirements, never as a mirror expected to restate the regulatory document itself. Some regulatory clause content only makes sense coming from the regulator and has no implementing counterpart to look for: statements about which OTHER entity types the guidance applies to, "this document does not constitute legislation"/disclaimer-of-legal-force language, or instructions addressed to supervisors or the regulator's own staff rather than to the regulated entity. When a regulatory clause is this kind of regulator-only content, its correct and expected internal-policy counterpart is that the internal document says nothing about it — this is NEVER a gap. Do not mark such a clause Partial Compliant or Non-Compliant merely because the internal manual (correctly, as a bank-facing document) omits it; mark it Compliant and note in uae_response_compliance_level that it is regulator-facing content with no implementing counterpart expected.

Vendor/list-provider due diligence (AML-CFT domain term) — when a regulatory clause requires "due diligence" on an external vendor or list provider used for sanctions/watchlist screening, this means verifying the accuracy and completeness of the data or list that vendor supplies (i.e. confirming the vendor's list actually contains all the required designated names) — it does NOT mean a general vendor-selection, procurement, or onboarding vetting process. If the internal policy states it ensures/verifies the vendor-supplied list's completeness against the required source lists, that satisfies this kind of requirement even without a separately documented vendor assessment or selection procedure. Do not require a vendor-vetting procedure the clause never actually asked for.

Element-level checking — when a regulatory clause enumerates multiple discrete required elements (e.g. a list of essential program components, a set of notification triggers, an enumerated list of factors to consider), do not form one holistic impression of the clause as a whole. Instead, go element by element: decide whether each individual element is covered in the internal policy text, and list every element's coverage (covered / not covered, with the specific supporting or missing evidence) in corrective_action_plan / fulfilled_clauses. Derive comply_status from the aggregate: Compliant only if every element is covered, Partial Compliant if some but not all are covered, Non-Compliant if none are covered.

Rules:
- Search EVERY attached internal document before concluding Non-Compliant. Compliant if ANY attached document fully addresses all elements/sub-obligations.
- Compare by regulatory meaning and operational control. Different wording is acceptable when the control is equivalent.
- Evidence quotes (uae_response_compliance_level and fulfilled_clauses) MUST be VERBATIM, character-for-character from the internal policy text. Do not paraphrase, summarize, or fix typos. If you cannot find any directly relevant text, use exactly: No corresponding procedure found. and lower confidence.
- Format each evidence line: [Document Name], Section [header or number], Page [N]: 'verbatim internal quote'
  Page [N] MUST be the 1-based PDF viewer page index. Include section numbers from document headers when present.
- comply_status must be one of: Compliant | Partial Compliant | Non-Compliant
- compliance_confidence_percentage: integer 0-100 aligned with status (Compliant 86-100, Partial Compliant 31-85, Non-Compliant 0-30). Calibrate honestly — prefer lower confidence when excerpts are weak or ambiguous rather than assuming coverage.
- fulfilled_clauses: one bullet (•) per sub-obligation/element that IS satisfied, with source ref:
  • [element summary] — [Document Name], Section [X], Page [N]: 'verbatim quote'
  Output None if nothing is covered.
- corrective_action_plan: MANDATORY non-empty when Partial or Non-Compliant. State (1) exactly what is missing, and (2) which document it was found in (if partially covered) or was not found in (if absent). For multi-element clauses, list each element's covered/not-covered status with evidence. Start with Gap(s): then numbered items (1) Missing: [sub-intent], Fix: [action]. Empty string when Compliant.
- suggested_responsibility: department or role for corrective action. Empty string when Compliant.
- reference_pdf: exact internal document file name(s) that contain the cited evidence. Comma-separate when multiple.
- Return structured JSON matching the provided schema only (no markdown fences)
""";
}
