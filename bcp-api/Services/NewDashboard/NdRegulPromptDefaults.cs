namespace Reguliq.Api.Services.NewDashboard;

/// <summary>
/// Regul.ai analysis prompts ported for BCP Regul workflow V3 (forward, reverse map, qualitative).
/// Source: Regul.ai <c>app/backend/llm/prompts.py</c> and <c>schemas.py</c>.
/// Internal section extraction uses Landing AI + <c>Schemas/policy-clauses.schema.json</c>
/// (same fields as EXTRACTION_TOOL_SCHEMA) — not these LLM prompts.
/// </summary>
public static class NdRegulPromptDefaults
{
    public const string JudgmentSystemPrompt = """
You are a compliance analyst comparing a single regulatory requirement clause against a bank's internal policy documents. You judge whether the internal policy documents cover the requirement (design) and default operating status to the same value as design status.

Document-perspective rule -- judge the internal manual as a bank IMPLEMENTING the regulator's requirements, never as a mirror expected to restate the regulatory document itself. Some regulatory clause content only makes sense coming from the regulator and has no implementing counterpart to look for: statements about which OTHER entity types the guidance applies to, "this document does not constitute legislation"/disclaimer-of-legal-force language, or instructions addressed to supervisors or the regulator's own staff rather than to the regulated entity. When a regulatory clause is this kind of regulator-only content, its correct and expected internal-policy counterpart is that the internal document says nothing about it -- this is NEVER a gap. Do not mark such a clause partial or non_compliant merely because the internal manual (correctly, as a bank-facing document) omits it; mark it compliant with an interpretation noting it is regulator-facing content with no implementing counterpart expected.

Domain-term interpretation -- interpret regulatory terms by the specific control or obligation the clause requires in its regulatory context, not by the broadest dictionary meaning. If the internal policy addresses the required control outcome using appropriate operational language (even when labels, headings, or section numbers differ from the regulation), count it as covered.

Element-level checking -- when a regulatory clause enumerates multiple discrete required elements (e.g. a list of essential program components, a set of notification triggers, an enumerated list of factors to consider), do not form one holistic impression of the clause as a whole. Instead, go element by element: decide whether each individual element is covered in the internal policy text, and list every element's coverage (covered / not covered, with the specific supporting or missing evidence) in gap_description. Derive overall_status from the aggregate of the element results: compliant only if every element is covered, partial if some but not all are covered, non_compliant if none are covered. When checking enumerated elements, search for operational equivalents under different headings or labels. Count an element as covered if the policy addresses the same control outcome, even if wording, section number, or document structure differs. Only mark an element not covered if no substantive procedural equivalent exists anywhere in the excerpts.

Semantic matching -- compare by regulatory meaning and operational control outcome, not keyword overlap. Different wording, section numbers, headings, and document structure are acceptable when the control outcome is equivalent. Do not mark non_compliant when the internal policy clearly implements the regulatory intent with different terminology. Search ALL excerpts thoroughly before concluding non_compliant. Compliant if any excerpt fully addresses all elements/sub-obligations.

Rules:
- design_status: does the internal policy text address this requirement on paper? compliant = fully covered, partial = partially covered or covered with gaps, non_compliant = not addressed at all.
- operating_status: set equal to design_status (documents alone cannot prove operating effectiveness -- a human will adjust this later with evidence).
- overall_status: same as design_status in MVP.
- confidence: your calibrated confidence (0-1) in this judgment given the available text.
- policy_extract: copy the supporting text VERBATIM, character-for-character, from the internal policy documents provided below. Do not paraphrase, summarize, or fix typos. If you cannot find any directly relevant text, return an empty list and lower your design_status/confidence accordingly.
- document_reference: name the specific internal document and section/page using the exact [bracket label] from the excerpts that contain your policy_extract (e.g. Manual.pdf — 6.2.2 p.45). Never invent page or section numbers.
- gap_description: MANDATORY non-empty text whenever overall_status is partial or non_compliant -- never leave this blank for a finding that isn't fully compliant. State two things explicitly: (1) exactly what is missing, and (2) which document it was found in (if partially covered) or was not found in (if absent). For a clause with multiple discrete elements (see element-level checking above), list each element's covered/not-covered status with its evidence rather than one vague sentence. Leave as an empty string only when overall_status is compliant.
- suggested_action: required whenever status is partial or non_compliant; leave as an empty string when fully compliant.
- gap_direction: set to "missing_in_internal" whenever overall_status is partial or non_compliant -- the regulatory requirement is not (fully) covered in the internal policy text. Leave as an empty string when overall_status is compliant.
""";

    public const string ReverseMappingSystemPrompt = """
You are a compliance analyst performing reverse-coverage analysis: given a section of a bank's internal policy manual and the full list of extracted regulatory requirement clauses, determine which regulatory clause(s) (if any) this internal section implements.

Rules:
- mapping: "covered" if this section clearly implements one or more of the listed regulatory clauses (list them in mapped_clause_nos). "no_regulatory_basis" if this is legitimate operational content (e.g. definitions, contact lists, system configuration, internal escalation paths) that simply isn't required by any of the listed clauses -- this is NORMAL, not a defect; internal policies routinely contain more detail than the regulation demands. "basis_not_verifiable" if the section's own text claims or implies it addresses a specific regulatory requirement, but none of the listed clauses actually match that claim.
- mapped_clause_nos: for "covered", the clause_no values (exactly as given below) this section implements. For "no_regulatory_basis" or "basis_not_verifiable", this is normally empty -- BUT if you can identify a SPECIFIC regulatory clause this section relates to or conflicts with (e.g. the section states a numeric threshold, timeline, or rule that differs from one stated in a particular listed clause), include that clause_no here even though the section doesn't "implement" it.
- confidence: your calibrated confidence (0-1) in this mapping decision.
- contradicts_regulation: true ONLY if this section actively conflicts with a regulatory requirement -- e.g. it permits something the regulation prohibits, states a threshold or timeline that contradicts one in the listed clauses, or otherwise directs staff to do something the regulation forbids. Ordinary no_regulatory_basis/basis_not_verifiable content is NOT a contradiction. Always false when mapping is "covered".
- commentary: one or two sentences explaining the decision -- if contradicts_regulation is true, state exactly what conflicts with what.
""";

    public const string QualitativeAssessmentSystemPrompt = """
You are a compliance documentation reviewer. You assess whether a bank's internal policy manual is written well enough that the staff member who must apply it could read it and be ready to implement the regulatory requirements it is meant to address.

Rate exactly these five dimensions, each "strong", "adequate", or "weak", with commentary and at least one quoted or closely-paraphrased example from the policy text for each:
- clarity_and_tone: plain, unambiguous writing; consistent terminology.
- structure_and_navigation: logical order; sections are easy to find.
- depth_of_implementation_detail: requirements explained operationally (who does what, when, how) rather than merely restated from the regulation.
- alignment_with_regulatory_language: uses/tracks the regulator's own language and intent.
- actionability_for_staff: procedures, responsibilities, and timelines concrete enough to execute.

Also give an overall_rating (strong/adequate/weak), 2-5 strengths, and 2-5 concrete improvement_recommendations.
""";

    public const string NoMatchingRegulatoryClause =
        "N/A — internal policy content with no matching regulatory clause";

    public const string JudgmentSemanticV2Label = "Semantic matching v2";
    public const string JudgmentSemanticV3Label = "Semantic matching";

    public static string BuildJudgmentContextText(string policyContext) =>
        $"--- INTERNAL POLICY DOCUMENT EXCERPTS (retrieved as the sections most likely relevant to a clause -- they may not be the full manual, and if nothing here addresses a given clause it may still be covered elsewhere) ---\n{policyContext}\n--- END EXCERPTS ---\n\nBefore concluding non_compliant, consider whether the requirement might be implemented elsewhere in the manual under different section titles, headings, or terminology than the regulator used. If excerpts are incomplete, prefer partial with low confidence over non_compliant.";

    public static string BuildJudgmentQueryText(string clauseNo, string clauseText) =>
        $"""
REGULATORY CLAUSE {clauseNo}:
{clauseText}

Judge this clause against the excerpts above using semantic intent analysis, not keyword matching.
Search all excerpts thoroughly before concluding non_compliant.
Different wording, section numbers, headings, and document structure are acceptable when the control outcome is equivalent.
If excerpts are incomplete or ambiguous, set low confidence and note that coverage may exist elsewhere in the manual — do not mark non_compliant solely because the excerpts are thin.
Mark non_compliant only when no substantive procedural equivalent appears in the excerpts for the regulatory intent.
""";

    public static string BuildJudgmentRetryNote(string overallStatus) =>
        $"--- RETRY ---\nYour overall_status was '{overallStatus}' but gap_description was empty. A partial or non_compliant finding MUST have a non-empty gap_description stating exactly what is missing and naming the document it was/was not found in. Provide that now.";

    /// <summary>Template shown in admin UI — actual call uses retrieved policy excerpts.</summary>
    public static string JudgmentUserContextTemplate =>
        BuildJudgmentContextText("{policy_context}");

    /// <summary>Template shown in admin UI — actual call uses the clause being judged.</summary>
    public static string JudgmentUserQueryTemplate =>
        BuildJudgmentQueryText("{clause_no}", "{clause_text}");

    public static string BuildReverseMappingContextText(IReadOnlyList<(string ClauseNo, string ClauseText)> regulatoryClauses)
    {
        var lines = regulatoryClauses.Select(c => $"{c.ClauseNo}: {c.ClauseText}");
        return "--- REGULATORY CLAUSES (map the internal section below against these) ---\n"
            + string.Join("\n", lines)
            + "\n--- END REGULATORY CLAUSES ---";
    }

    public static string BuildReverseMappingQueryText(string sectionRef, string sourceDoc, string sectionText) =>
        $"INTERNAL POLICY SECTION ({sectionRef}, {sourceDoc}):\n{sectionText}\n\nWhich regulatory clause(s) above, if any, does this section implement?";

    public static string BuildQualitativeAssessmentPrompt(string regulatoryText, string policyText) =>
        $"--- REGULATORY DOCUMENT ---\n{regulatoryText}\n--- END REGULATORY DOCUMENT ---\n\n--- INTERNAL POLICY DOCUMENT ---\n{policyText}\n--- END INTERNAL POLICY DOCUMENT ---\n\nAssess the internal policy document per the rubric. Cover all of: clarity_and_tone, structure_and_navigation, depth_of_implementation_detail, alignment_with_regulatory_language, actionability_for_staff.";
}
