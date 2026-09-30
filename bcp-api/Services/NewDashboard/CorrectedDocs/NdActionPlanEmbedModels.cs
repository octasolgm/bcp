namespace Reguliq.Api.Services.NewDashboard.CorrectedDocs;

/// <summary>
/// One resolved gap's action plan, resolved down to a specific internal document and (where known)
/// a page in it — the unit <see cref="NdActionPlanEmbedResolver"/> produces and the PDF/DOCX
/// embedders consume to write it into the corrected copy.
/// </summary>
public sealed record NdActionPlanEmbedTarget(
    Guid StoredDocumentId,
    /// <summary>1-based PDF page the evidence for this clause was found on, when known. Null means
    /// the source document/page could not be resolved with confidence — the embedder appends the
    /// note at the end of the document instead of guessing a location.</summary>
    int? Page,
    /// <summary>Best-effort anchor text for DOCX documents (a heading or a short excerpt of the cited
    /// passage) — used to find the paragraph to insert after. Null falls back to the document end.</summary>
    string? AnchorText,
    string ClauseNo,
    string? ClauseTitle,
    string GapText,
    string ActionText,
    string? ResponsibilityLabel,
    DateTimeOffset ResolvedAt,
    string? ResolvedByName,
    /// <summary>Forward-judgment gap description when available — often clearer than the CAP slice.</summary>
    string? JudgmentGapDescription = null,
    IReadOnlyList<string>? PolicyExtracts = null,
    /// <summary>LLM-generated policy prose; when set, <see cref="NdActionPlanEmbedNote.Build"/> embeds this instead of the raw action plan.</summary>
    string? GeneratedEmbedBody = null);

/// <summary>One target document's embed job: every resolved action that traced back to it, grouped so
/// the embedders can batch everything landing on the same page/anchor into one inserted block.</summary>
public sealed record NdActionPlanEmbedJob(Guid StoredDocumentId, IReadOnlyList<NdActionPlanEmbedTarget> Targets);

public static class NdActionPlanEmbedNote
{
    /// <summary>
    /// The text written into the corrected copy for one resolved action plan. Always names the exact
    /// clause it closes, so a reader lands on the note and immediately knows which regulatory
    /// requirement it satisfies — the whole point of embedding it in the first place.
    /// </summary>
    public static string Build(NdActionPlanEmbedTarget t)
    {
        if (!string.IsNullOrWhiteSpace(t.GeneratedEmbedBody))
            return BuildFromGeneratedPolicy(t, t.GeneratedEmbedBody.Trim());

        return BuildLegacyActionPlanNote(t);
    }

    /// <summary>Short policy fallback when the finalize LLM is unavailable.</summary>
    public static string BuildLegacyPolicyFallback(NdActionPlanEmbedTarget t)
    {
        var action = CleanOneLine(t.ActionText);
        if (action.Length > 480) action = action[..477] + "…";
        return action;
    }

    private static string BuildFromGeneratedPolicy(NdActionPlanEmbedTarget t, string body)
    {
        var clauseLabel = ClauseLabel(t);
        var lines = new List<string>
        {
            $"POLICY UPDATE — Regulatory Clause {clauseLabel}",
            "",
            body,
            "",
            FulfillmentFooter(t),
        };
        return string.Join('\n', lines);
    }

    private static string BuildLegacyActionPlanNote(NdActionPlanEmbedTarget t)
    {
        var clauseLabel = ClauseLabel(t);
        var lines = new List<string>
        {
            $"COMPLIANCE ACTION — Clause {clauseLabel}",
            "",
            $"Gap identified: {CleanOneLine(t.GapText)}",
            "",
            $"Action taken: {CleanOneLine(t.ActionText)}",
        };
        var meta = new List<string>();
        if (!string.IsNullOrWhiteSpace(t.ResponsibilityLabel)) meta.Add($"Responsible: {t.ResponsibilityLabel}");
        meta.Add($"Resolved: {t.ResolvedAt:dd MMM yyyy}" + (string.IsNullOrWhiteSpace(t.ResolvedByName) ? "" : $" by {t.ResolvedByName}"));
        lines.Add("");
        lines.Add(string.Join("   ", meta));
        lines.Add("");
        lines.Add(FulfillmentFooter(t));
        return string.Join('\n', lines);
    }

    private static string ClauseLabel(NdActionPlanEmbedTarget t) =>
        string.IsNullOrWhiteSpace(t.ClauseTitle) ? t.ClauseNo : $"{t.ClauseNo} — {t.ClauseTitle}";

    private static string FulfillmentFooter(NdActionPlanEmbedTarget t) =>
        t.Page is > 0
            ? $"Note: This update addresses Regulatory Clause {t.ClauseNo}, referenced at p.{t.Page} of this document."
            : $"Note: This update addresses Regulatory Clause {t.ClauseNo}.";

    private static string CleanOneLine(string? s) =>
        string.IsNullOrWhiteSpace(s) ? "—" : s.Trim().Replace('\r', ' ').Replace('\n', ' ');
}
