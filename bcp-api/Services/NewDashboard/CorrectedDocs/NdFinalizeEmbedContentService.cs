using Reguliq.Api.Services.Llm;

namespace Reguliq.Api.Services.NewDashboard.CorrectedDocs;

/// <summary>
/// On reviewer finalize, turns a resolved gap + action into policy language suitable to embed in the
/// corrected internal document — not the raw action-plan checklist the checker typed.
/// </summary>
public class NdFinalizeEmbedContentService(
    FinalizeEmbedLlmService llm,
    ILogger<NdFinalizeEmbedContentService> logger)
{
    private const string SystemInstruction =
        """
        You are a senior compliance policy writer for a regulated financial institution.
        The bank closed a regulatory gap found during an AML/compliance analysis. Write ONLY the policy
        language that should be inserted into the internal document so a future automated review will
        treat the requirement as satisfied.

        Rules:
        - Write as finished policy manual text (definitions, obligations, controls) — not a project plan.
        - Do NOT include operational instructions such as re-run the assessment, present for approval,
          feed revised ratings, update methodology to reflect numbered gap bullets, or similar workflow steps.
        - Do NOT paste the gap-analysis checklist or numbered audit findings verbatim.
        - Do NOT mention "gap", "action plan", "resolved", or analysis metadata.
        - Use professional third-person policy tone; complete sentences; 1–4 short paragraphs or a tight
          definition list as appropriate.
        - Output plain text only (no markdown fences, no JSON).
        """;

    public async Task<string> GenerateEmbedBodyAsync(NdActionPlanEmbedTarget target, CancellationToken ct)
    {
        try
        {
            var prompt = BuildPrompt(target);
            var raw = await llm.AnalyzeTextAsync(prompt, ct);
            var cleaned = CleanModelOutput(raw);
            if (string.IsNullOrWhiteSpace(cleaned))
                return NdActionPlanEmbedNote.BuildLegacyPolicyFallback(target);
            return cleaned;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Finalize embed LLM failed for clause {Clause} — using fallback policy text",
                target.ClauseNo);
            return NdActionPlanEmbedNote.BuildLegacyPolicyFallback(target);
        }
    }

    internal static string BuildPrompt(NdActionPlanEmbedTarget target)
    {
        var clauseLabel = string.IsNullOrWhiteSpace(target.ClauseTitle)
            ? target.ClauseNo
            : $"{target.ClauseNo} — {target.ClauseTitle}";
        var gap = PreferNonEmpty(target.JudgmentGapDescription, target.GapText);
        var parts = new List<string>
        {
            SystemInstruction,
            "",
            "---",
            $"Regulatory clause: {clauseLabel}",
            "",
            "What was missing (for your understanding only — do not repeat as a checklist):",
            gap,
            "",
            "What the bank implemented (context only — distill into policy prose, do not copy verbatim):",
            target.ActionText.Trim(),
        };

        var extracts = target.PolicyExtracts ?? [];
        if (extracts.Count > 0)
        {
            parts.Add("");
            parts.Add("Existing policy excerpts from this document (match tone where helpful):");
            foreach (var q in extracts.Take(5))
            {
                if (!string.IsNullOrWhiteSpace(q))
                    parts.Add($"- {q.Trim()}");
            }
        }

        parts.Add("");
        parts.Add("Write the policy text to embed:");
        return string.Join('\n', parts);
    }

    private static string PreferNonEmpty(string? a, string b) =>
        !string.IsNullOrWhiteSpace(a) ? a.Trim() : b.Trim();

    private static string CleanModelOutput(string raw)
    {
        var t = raw.Trim();
        if (t.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNl = t.IndexOf('\n');
            if (firstNl >= 0) t = t[(firstNl + 1)..];
            if (t.EndsWith("```", StringComparison.Ordinal)) t = t[..^3];
            t = t.Trim();
        }
        return t;
    }
}
