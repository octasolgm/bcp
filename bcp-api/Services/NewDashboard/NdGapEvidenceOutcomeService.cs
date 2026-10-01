using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;

namespace Reguliq.Api.Services.NewDashboard;

/// <summary>
/// After a gap-evidence re-judgment, keeps the original gap on record, appends fulfillment
/// detail, updates policy extracts, resolves or splits action plans, and writes history rows.
/// </summary>
public sealed class NdGapEvidenceOutcomeService(AppDbContext db)
{
    public sealed record EvidencePriorState(
        string OriginalGapRecord,
        string? PriorFinalStatus,
        List<string> PriorPolicyExtracts,
        string? PriorDocumentReference,
        string? PriorJudgmentJson);

    public EvidencePriorState CapturePriorState(NdAnalysisPoint point, NdRegulForwardFinding? finding)
    {
        RegulJudgmentResult? priorJudgment = null;
        if (!string.IsNullOrWhiteSpace(finding?.ResultJson))
        {
            try
            {
                priorJudgment = JsonSerializer.Deserialize<RegulJudgmentResult>(finding.ResultJson);
            }
            catch
            {
                /* ignore malformed */
            }
        }

        var gapRecord = ResolveGapRecordText(point, priorJudgment);
        var extracts = priorJudgment?.PolicyExtract?
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .ToList() ?? [];

        return new EvidencePriorState(
            gapRecord,
            point.FinalStatus,
            extracts,
            priorJudgment?.DocumentReference?.Trim(),
            finding?.ResultJson);
    }

    public async Task ApplyRegulEvidenceOutcomeAsync(
        NdAnalysisRun run,
        NdAnalysisPoint point,
        NdRegulForwardFinding finding,
        RegulJudgmentResult judgment,
        EvidencePriorState prior,
        IReadOnlyList<string> evidenceDocNames,
        Guid? changedBy,
        int? actionIndexFilter,
        CancellationToken ct)
    {
        var merged = MergeJudgmentForRecord(prior, judgment, evidenceDocNames);
        var landingMessage = NdRegulJudgmentFormatter.FormatLandingMessage(
            finding.ClauseNo, finding.ClauseText, merged);

        finding.ResultJson = JsonSerializer.Serialize(merged);
        NdRegulAnalysisPointSync.ApplyForwardJudgment(point, merged, landingMessage);

        await AppendGapTextHistoryAsync(point, prior, merged, evidenceDocNames, changedBy, ct);
        await ApplyActionPlanOutcomesAsync(point, merged, evidenceDocNames, changedBy, actionIndexFilter, ct);
        await NdGapStatusResolver.RecomputeAsync(db, [point.Id], ct);
    }

    public static RegulJudgmentResult MergeJudgmentForRecord(
        EvidencePriorState prior,
        RegulJudgmentResult fresh,
        IReadOnlyList<string> evidenceDocNames)
    {
        var docLabel = evidenceDocNames.Count == 0
            ? "uploaded gap evidence"
            : string.Join("; ", evidenceDocNames.Distinct(StringComparer.OrdinalIgnoreCase));

        var reviewBody = BuildEvidenceReviewBody(prior, fresh, docLabel);
        var mergedGap = ComposeGapRecord(prior.OriginalGapRecord, reviewBody);

        fresh.GapDescription = mergedGap;
        if (IsFullyCompliant(fresh.OverallStatus) && string.IsNullOrWhiteSpace(fresh.SuggestedAction))
            fresh.SuggestedAction = "N/A";

        fresh.PolicyExtract = BuildMergedPolicyExtracts(prior, fresh, docLabel);
        if (string.IsNullOrWhiteSpace(fresh.DocumentReference))
            fresh.DocumentReference = docLabel;
        else if (!fresh.DocumentReference.Contains(docLabel, StringComparison.OrdinalIgnoreCase))
            fresh.DocumentReference = $"{fresh.DocumentReference.Trim()} | Evidence: {docLabel}";

        return fresh;
    }

    private static string BuildEvidenceReviewBody(
        EvidencePriorState prior,
        RegulJudgmentResult fresh,
        string docLabel)
    {
        var stamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm") + " UTC";
        var statusLabel = NdRegulJudgmentFormatter.MapDisplayStatus(fresh.OverallStatus, fresh.DesignStatus);

        if (IsFullyCompliant(fresh.OverallStatus))
        {
            var summary = !string.IsNullOrWhiteSpace(fresh.Interpretation)
                ? fresh.Interpretation.Trim()
                : $"The uploaded document \"{docLabel}\" now addresses the original gap.";
            return $"""
Evidence review ({stamp}) — {docLabel}
Outcome: Gap fulfilled by uploaded evidence ({statusLabel}).
{summary}
""".Trim();
        }

        var freshDetail = fresh.GapDescription?.Trim();
        if (string.IsNullOrWhiteSpace(freshDetail))
            freshDetail = fresh.Interpretation?.Trim()
                ?? $"Partial coverage found in \"{docLabel}\"; see element list below.";

        var remaining = fresh.SuggestedAction?.Trim();
        if (!string.IsNullOrWhiteSpace(remaining) && remaining is not ("N/A" or "—" or "-"))
        {
            freshDetail += $"\n\nRemaining pending (corrective focus): {remaining}";
        }

        return $"""
Evidence review ({stamp}) — {docLabel}
Outcome: Partial fulfillment ({statusLabel}).
Covered vs still pending:
{freshDetail}
""".Trim();
    }

    private static string ComposeGapRecord(string originalGapRecord, string reviewBody)
    {
        var original = string.IsNullOrWhiteSpace(originalGapRecord)
            ? "(Original gap text was not captured — see analysis history.)"
            : originalGapRecord.Trim();

        return $"""
--- Original gap (on record) ---
{original}

--- {reviewBody} ---
""".Trim();
    }

    private static List<string> BuildMergedPolicyExtracts(
        EvidencePriorState prior,
        RegulJudgmentResult fresh,
        string docLabel)
    {
        var list = new List<string>();
        var refLabel = string.IsNullOrWhiteSpace(fresh.DocumentReference) ? docLabel : fresh.DocumentReference.Trim();

        foreach (var quote in fresh.PolicyExtract.Where(q => !string.IsNullOrWhiteSpace(q)))
        {
            var q = quote.Trim();
            if (!q.StartsWith('['))
                list.Add($"[{refLabel}] {q}");
            else
                list.Add(q);
        }

        foreach (var old in prior.PriorPolicyExtracts.Take(3))
        {
            var tagged = old.StartsWith("[Prior analysis]", StringComparison.OrdinalIgnoreCase)
                ? old
                : $"[Prior analysis] {old}";
            if (!list.Contains(tagged, StringComparer.OrdinalIgnoreCase))
                list.Add(tagged);
        }

        return list;
    }

    private async Task AppendGapTextHistoryAsync(
        NdAnalysisPoint point,
        EvidencePriorState prior,
        RegulJudgmentResult merged,
        IReadOnlyList<string> evidenceDocNames,
        Guid? changedBy,
        CancellationToken ct)
    {
        var content = merged.GapDescription?.Trim();
        if (string.IsNullOrWhiteSpace(content)) return;

        var prev = await db.NdActionPlanHistories
            .Where(h => h.AnalysisPointId == point.Id && h.IsCurrent)
            .ToListAsync(ct);
        foreach (var h in prev) h.IsCurrent = false;

        var maxVersion = await db.NdActionPlanHistories
            .Where(h => h.AnalysisPointId == point.Id)
            .MaxAsync(h => (int?)h.VersionNumber, ct) ?? 0;

        db.NdActionPlanHistories.Add(new NdActionPlanHistory
        {
            AnalysisPointId = point.Id,
            ActionPlanContent = content,
            VersionNumber = maxVersion + 1,
            ChangeType = "evidence_rerun",
            ChangedBy = changedBy,
            IsCurrent = true,
        });

        point.FinalActionPlan = content;
        point.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private async Task ApplyActionPlanOutcomesAsync(
        NdAnalysisPoint point,
        RegulJudgmentResult judgment,
        IReadOnlyList<string> evidenceDocNames,
        Guid? changedBy,
        int? actionIndexFilter,
        CancellationToken ct)
    {
        var query = db.NdAnalysisActionPlans
            .Where(p => p.AnalysisPointId == point.Id && p.Status != ActionPlanStatuses.Resolved);

        var plans = await query.OrderBy(p => p.GapIndex).ThenBy(p => p.SortOrder).ToListAsync(ct);
        if (plans.Count == 0) return;

        if (actionIndexFilter.HasValue)
        {
            var idx = actionIndexFilter.Value;
            var filtered = new List<NdAnalysisActionPlan>();
            for (var i = 0; i < plans.Count; i++)
            {
                var p = plans[i];
                if (p.GapIndex == idx || (p.GapIndex == 0 && idx == 1) || i + 1 == idx)
                    filtered.Add(p);
            }
            plans = filtered;
            if (plans.Count == 0) return;
        }

        var docLabel = evidenceDocNames.FirstOrDefault() ?? "uploaded evidence";
        var gapText = judgment.GapDescription ?? "";
        var coveredText = ExtractCoverageText(gapText, covered: true);
        var pendingText = ExtractCoverageText(gapText, covered: false);

        if (IsFullyCompliant(judgment.OverallStatus))
        {
            foreach (var plan in plans)
                await ResolvePlanAsync(plan, changedBy, $"Fulfilled by {docLabel} (full gap closure).", ct);
            return;
        }

        if (!IsPartialCompliant(judgment.OverallStatus) && !IsFullyCompliant(judgment.OverallStatus))
            return;

        foreach (var plan in plans)
        {
            var outcome = ClassifyPlanOutcome(plan.ActionPlan, coveredText, pendingText);
            switch (outcome.Kind)
            {
                case PlanOutcomeKind.Full:
                    await ResolvePlanAsync(
                        plan,
                        changedBy,
                        $"Fulfilled by {docLabel}: {outcome.FulfilledSnippet}",
                        ct);
                    break;
                case PlanOutcomeKind.Partial:
                    await SplitPlanAsync(plan, outcome.FulfilledSnippet!, outcome.RemainingSnippet!, docLabel, changedBy, ct);
                    break;
                case PlanOutcomeKind.None:
                    break;
            }
        }
    }

    private enum PlanOutcomeKind { None, Full, Partial }

    private sealed record PlanOutcomeClassification(PlanOutcomeKind Kind, string? FulfilledSnippet, string? RemainingSnippet);

    private static PlanOutcomeClassification ClassifyPlanOutcome(
        string actionPlanText,
        string coveredCorpus,
        string pendingCorpus)
    {
        var sentences = SplitSentences(actionPlanText);
        if (sentences.Count == 0)
            return new PlanOutcomeClassification(PlanOutcomeKind.None, null, null);

        var fulfilled = new List<string>();
        var remaining = new List<string>();

        foreach (var sentence in sentences)
        {
            var scoreCovered = TextOverlapScore(sentence, coveredCorpus);
            var scorePending = TextOverlapScore(sentence, pendingCorpus);
            if (scoreCovered >= 0.45 && scoreCovered >= scorePending + 0.1)
                fulfilled.Add(sentence);
            else if (scorePending >= 0.35 && scorePending > scoreCovered)
                remaining.Add(sentence);
            else if (scoreCovered >= 0.55)
                fulfilled.Add(sentence);
            else
                remaining.Add(sentence);
        }

        if (fulfilled.Count == 0)
            return new PlanOutcomeClassification(PlanOutcomeKind.None, null, null);
        if (remaining.Count == 0)
            return new PlanOutcomeClassification(PlanOutcomeKind.Full, string.Join(" ", fulfilled), null);

        return new PlanOutcomeClassification(
            PlanOutcomeKind.Partial,
            string.Join(" ", fulfilled),
            string.Join(" ", remaining));
    }

    private async Task ResolvePlanAsync(
        NdAnalysisActionPlan plan,
        Guid? changedBy,
        string reason,
        CancellationToken ct)
    {
        if (plan.Status == ActionPlanStatuses.Resolved) return;
        var now = DateTimeOffset.UtcNow;
        db.NdAnalysisActionPlanStatusHistories.Add(new NdAnalysisActionPlanStatusHistory
        {
            ActionPlanId = plan.Id,
            PreviousStatus = plan.Status,
            NewStatus = ActionPlanStatuses.Resolved,
            ChangedBy = changedBy,
        });
        plan.Status = ActionPlanStatuses.Resolved;
        plan.ResolvedAt = now;
        plan.ResolvedBy = changedBy;
        plan.UpdatedBy = changedBy;
        plan.UpdatedAt = now;
        var note = reason.Trim();
        plan.Comment = string.IsNullOrWhiteSpace(plan.Comment) ? note : $"{plan.Comment}\n{note}";
        await db.SaveChangesAsync(ct);
    }

    private async Task SplitPlanAsync(
        NdAnalysisActionPlan plan,
        string fulfilledPart,
        string remainingPart,
        string docLabel,
        Guid? changedBy,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await ResolvePlanAsync(plan, changedBy, $"Partially fulfilled by {docLabel}: {fulfilledPart}", ct);

        var maxOrder = await db.NdAnalysisActionPlans
            .Where(p => p.AnalysisPointId == plan.AnalysisPointId && p.GapIndex == plan.GapIndex)
            .Select(p => (int?)p.SortOrder)
            .MaxAsync(ct) ?? plan.SortOrder;

        var followUp = new NdAnalysisActionPlan
        {
            AnalysisRunId = plan.AnalysisRunId,
            AnalysisPointId = plan.AnalysisPointId,
            GapIndex = plan.GapIndex,
            ActionPlan = remainingPart.Trim(),
            Status = ActionPlanStatuses.Pending,
            PriorityScore = plan.PriorityScore,
            Priority = plan.Priority,
            TargetDate = plan.TargetDate,
            ResponsibilityType = plan.ResponsibilityType,
            ResponsibilityDepartmentId = plan.ResponsibilityDepartmentId,
            ResponsibilityUserId = plan.ResponsibilityUserId,
            ResponsibilityLabel = plan.ResponsibilityLabel,
            Comment = $"Split from resolved action after evidence review ({docLabel}).",
            SortOrder = maxOrder + 1,
            CreatedBy = changedBy,
            UpdatedBy = changedBy,
        };
        db.NdAnalysisActionPlans.Add(followUp);
        await db.SaveChangesAsync(ct);

        db.NdAnalysisActionPlanStatusHistories.Add(new NdAnalysisActionPlanStatusHistory
        {
            ActionPlanId = followUp.Id,
            PreviousStatus = null,
            NewStatus = ActionPlanStatuses.Pending,
            ChangedBy = changedBy,
        });
        await db.SaveChangesAsync(ct);
    }

    private static string ExtractCoverageText(string gapDescription, bool covered)
    {
        var lines = gapDescription.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var hits = new List<string>();
        foreach (var line in lines)
        {
            var t = line.Trim();
            if (covered)
            {
                if (Regex.IsMatch(t, @"\b(covered|fulfilled|addressed)\b", RegexOptions.IgnoreCase)
                    && !Regex.IsMatch(t, @"\b(missing|not covered|pending|partially covered)\b", RegexOptions.IgnoreCase))
                    hits.Add(t);
                else if (Regex.IsMatch(t, @"partially covered", RegexOptions.IgnoreCase))
                    hits.Add(t);
            }
            else
            {
                if (Regex.IsMatch(t, @"\b(missing|not covered|pending|remaining|partially covered)\b", RegexOptions.IgnoreCase))
                    hits.Add(t);
            }
        }

        return string.Join("\n", hits);
    }

    private static List<string> SplitSentences(string text)
    {
        return Regex.Split(text.Trim(), @"(?<=[.;])\s+")
            .Select(s => s.Trim())
            .Where(s => s.Length > 8)
            .ToList();
    }

    private static double TextOverlapScore(string sentence, string corpus)
    {
        if (string.IsNullOrWhiteSpace(corpus)) return 0;
        var words = Regex.Matches(sentence.ToLowerInvariant(), @"[a-z0-9]{4,}")
            .Select(m => m.Value)
            .Distinct()
            .ToList();
        if (words.Count == 0) return 0;
        var hits = words.Count(w => corpus.Contains(w, StringComparison.OrdinalIgnoreCase));
        return (double)hits / words.Count;
    }

    private static string ResolveGapRecordText(NdAnalysisPoint point, RegulJudgmentResult? priorJudgment)
    {
        foreach (var candidate in new[]
                 {
                     point.FinalActionPlan,
                     point.OriginalAiActionPlan,
                     priorJudgment?.GapDescription,
                     point.LandingAiActionPlan,
                 })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var t = candidate.Trim();
            if (t is "N/A" or "—" or "-") continue;
            if (t.Contains("--- Original gap (on record) ---", StringComparison.OrdinalIgnoreCase))
            {
                var original = ExtractOriginalGapSection(t);
                if (!string.IsNullOrWhiteSpace(original)) return original;
            }
            return t;
        }

        return "";
    }

    private static string ExtractOriginalGapSection(string mergedText)
    {
        const string marker = "--- Original gap (on record) ---";
        var idx = mergedText.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return mergedText;
        var after = mergedText[(idx + marker.Length)..];
        var end = after.IndexOf("--- Evidence review", StringComparison.OrdinalIgnoreCase);
        if (end < 0) end = after.IndexOf("--- ", StringComparison.Ordinal);
        var slice = end > 0 ? after[..end] : after;
        return slice.Trim();
    }

    private static bool IsFullyCompliant(string? overallStatus) =>
        string.Equals(overallStatus?.Trim(), "compliant", StringComparison.OrdinalIgnoreCase);

    private static bool IsPartialCompliant(string? overallStatus)
    {
        var s = (overallStatus ?? "").Trim().ToLowerInvariant();
        return s.Contains("partial");
    }
}
