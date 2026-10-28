using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;

namespace Reguliq.Api.Services.NewDashboard;

/// <summary>
/// Clause evals: frozen reference copies of single clauses' AI results (versioned per clause, one current
/// version each), and the local, rule-based comparison of an analysis's clauses against them. No AI is used
/// to compare: verdicts are compared directly and gap wording by word overlap.
///
/// Which prompt versions produced a clause is read from that clause's own AI call log (regul_clause_traces),
/// not from whatever is current today: the system prompt, the filled user block 1 (context) and the filled user
/// block 2 (query) actually sent are matched against every stored version's template. So a run whose clauses
/// were re-run under different prompts records each clause's real versions.
/// </summary>
public class NdAnalysisEvalService(AppDbContext db, ILogger<NdAnalysisEvalService> logger)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public sealed record PromptVersionRef(string PromptKey, int VersionNumber, string Label);

    public sealed record PromptVersionSnapshot(string PromptKey, int VersionNumber, string Label, string PromptText);

    public sealed class EvalClause
    {
        public string ClauseNo { get; set; } = "";
        public string ClauseTitle { get; set; } = "";
        public string ClauseText { get; set; } = "";
        public Guid? RegulationDocumentId { get; set; }
        public string? RegulationName { get; set; }
        /// <summary>completed | failed | pending ... (the clause's judgment state when snapshotted)</summary>
        public string FindingStatus { get; set; } = "";
        public string? Error { get; set; }
        public string OverallStatus { get; set; } = "";
        public double Confidence { get; set; }
        public string Interpretation { get; set; } = "";
        public string CoveredElements { get; set; } = "";
        public string DocumentReference { get; set; } = "";
        public List<string> PolicyExtract { get; set; } = [];
        public string GapDescription { get; set; } = "";
        public string SuggestedAction { get; set; } = "";
        public List<string> Gaps { get; set; } = [];
        public List<string> Actions { get; set; } = [];
        public string? Provider { get; set; }
        public string? Model { get; set; }
        /// <summary>Prompt versions that produced this clause; empty when it has no AI call log.</summary>
        public List<PromptVersionRef> PromptVersions { get; set; } = [];
        public bool ClauseContextSent { get; set; }
        /// <summary>Retrieval pipeline version behind this clause's evidence; null when the run's engine has no
        /// retrieval step (full-markdown engines).</summary>
        public int? PipelineVersion { get; set; }
        public DateTimeOffset? JudgedAt { get; set; }
    }

    public sealed record RunSnapshot(
        NdAnalysisRun Run,
        IReadOnlyList<EvalClause> Clauses,
        IReadOnlyList<PromptVersionSnapshot> PromptVersions);

    // ---------------------------------------------------------------- snapshot

    public async Task<RunSnapshot?> SnapshotRunAsync(Guid runId, CancellationToken ct)
    {
        var run = await db.NdAnalysisRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run == null) return null;

        var findings = await db.NdRegulForwardFindings.AsNoTracking()
            .Where(f => f.AnalysisRunId == runId && !f.ClauseNo.StartsWith(NdRegulReverseIntRows.IntClausePrefix))
            .ToListAsync(ct);

        var pointIds = findings.Where(f => f.AnalysisPointId.HasValue).Select(f => f.AnalysisPointId!.Value).ToList();
        var sources = await (
                from ap in db.NdAnalysisPoints.AsNoTracking()
                join rp in db.NdRegulationPoints.AsNoTracking() on ap.RegulationPointId equals rp.Id
                join rd in db.NdRegulationDocuments.AsNoTracking() on rp.RegulationDocumentId equals rd.Id
                where pointIds.Contains(ap.Id)
                select new
                {
                    ap.Id,
                    rp.PointNumber,
                    rp.PointTitle,
                    ContentStart = rp.PointContent.Length > 400 ? rp.PointContent.Substring(0, 400) : rp.PointContent,
                    rp.RegulationDocumentId,
                    rd.Name,
                })
            .ToDictionaryAsync(x => x.Id, ct);

        var traces = await db.NdRegulClauseTraces.AsNoTracking()
            .Where(t => t.AnalysisRunId == runId
                && (t.Step == RegulClauseTraceSteps.Context || t.Step == RegulClauseTraceSteps.LlmCall))
            .Select(t => new TraceRow(t.ClauseNo, t.Step, t.Provider, t.Model, t.SystemPrompt, t.ContextText,
                t.QueryText, t.ClauseContextSent, t.CreatedAt))
            .ToListAsync(ct);
        var tracesByClause = traces.GroupBy(t => t.ClauseNo).ToDictionary(g => g.Key, g => g.OrderBy(t => t.CreatedAt).ToList());

        var versions = await db.NdAnalysisPromptVersions.AsNoTracking()
            .Where(v => v.PromptKey.StartsWith("regul_judgment_"))
            .ToListAsync(ct);

        var used = new Dictionary<(string, int), PromptVersionSnapshot>();
        var clauses = new List<EvalClause>();
        foreach (var f in findings.OrderBy(f => f.ClauseNo, ClauseNoComparer.Instance))
        {
            RegulJudgmentResult? result = null;
            if (!string.IsNullOrWhiteSpace(f.ResultJson))
            {
                try { result = JsonSerializer.Deserialize<RegulJudgmentResult>(f.ResultJson); }
                catch (JsonException) { result = null; }
            }

            var source = f.AnalysisPointId is Guid pid && sources.TryGetValue(pid, out var src) ? src : null;
            var clause = new EvalClause
            {
                ClauseNo = f.ClauseNo,
                ClauseTitle = source == null
                    ? ""
                    : NdRegulClauseContextService.HeadingTitle(
                        NdRegulClauseContextService.NormalizeNumber(source.PointNumber) ?? source.PointNumber,
                        source.PointTitle, source.ContentStart),
                ClauseText = f.ClauseText,
                RegulationDocumentId = source?.RegulationDocumentId,
                RegulationName = source?.Name,
                FindingStatus = f.Status,
                Error = f.ErrorMessage,
                PipelineVersion = PipelineVersionOf(f.RetrievalJson),
                JudgedAt = f.UpdatedAt,
            };
            if (result != null)
            {
                clause.OverallStatus = NormalizeStatus(result.OverallStatus);
                clause.Confidence = result.Confidence;
                clause.Interpretation = result.Interpretation;
                clause.CoveredElements = result.CoveredElements;
                clause.DocumentReference = result.DocumentReference;
                clause.PolicyExtract = result.PolicyExtract;
                clause.GapDescription = result.GapDescription;
                clause.SuggestedAction = result.SuggestedAction;
                clause.Gaps = NumberedItems(result.GapDescription);
                clause.Actions = NumberedItems(result.SuggestedAction);
            }

            if (tracesByClause.TryGetValue(f.ClauseNo, out var clauseTraces))
                ApplyTraceInfo(clause, clauseTraces, versions, used);
            clauses.Add(clause);
        }

        var promptVersions = used.Values
            .OrderBy(v => v.PromptKey, StringComparer.Ordinal).ThenBy(v => v.VersionNumber)
            .ToList();
        return new RunSnapshot(run, clauses, promptVersions);
    }

    /// <summary>The pipeline version stored on a clause's retrieval record; records from before versions
    /// existed are v1. Null when the clause has no retrieval record.</summary>
    public static int? PipelineVersionOf(string? retrievalJson)
    {
        if (string.IsNullOrWhiteSpace(retrievalJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(retrievalJson);
            return doc.RootElement.TryGetProperty("pipelineVersion", out var v) && v.TryGetInt32(out var n)
                ? n
                : NdRegulPipelineVersions.V1;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string? DescribePipelines(IEnumerable<EvalClause> clauses)
    {
        var versions = clauses.Where(c => c.PipelineVersion.HasValue).Select(c => c.PipelineVersion!.Value)
            .Distinct().OrderBy(v => v).ToList();
        return versions.Count == 0 ? null : string.Join(", ", versions.Select(v => $"v{v}"));
    }

    private sealed record TraceRow(
        string ClauseNo, string Step, string? Provider, string? Model, string? SystemPrompt, string? ContextText,
        string? QueryText, bool ClauseContextSent, DateTimeOffset CreatedAt);

    /// <summary>Uses the clause's latest judgment: its last Step 7 context trace and the first AI call after it.</summary>
    private static void ApplyTraceInfo(
        EvalClause clause,
        List<TraceRow> traces,
        List<NdAnalysisPromptVersion> versions,
        Dictionary<(string, int), PromptVersionSnapshot> used)
    {
        var context = traces.LastOrDefault(t => t.Step == RegulClauseTraceSteps.Context);
        var calls = traces.Where(t => t.Step == RegulClauseTraceSteps.LlmCall
            && (context == null || t.CreatedAt >= context.CreatedAt)).ToList();
        var firstCall = calls.FirstOrDefault();
        var lastCall = calls.LastOrDefault();
        clause.Provider = lastCall?.Provider;
        clause.Model = lastCall?.Model;
        clause.ClauseContextSent = context?.ClauseContextSent ?? false;
        var at = context?.CreatedAt ?? firstCall?.CreatedAt ?? DateTimeOffset.UtcNow;

        // V3/V5 and full-markdown runs use different prompt keys; whichever set matches is the one used.
        var system = calls.Select(c => c.SystemPrompt).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
        foreach (var prefix in new[] { "regul_judgment_", "regul_judgment_full_" })
        {
            var parts = new (string Key, string? Filled)[]
            {
                (prefix + "system", system),
                (prefix + "user_context", context?.ContextText),
                (prefix + "user_query", firstCall?.QueryText),
            };
            var candidates = parts
                .Select(p => (p.Key, Matches: string.IsNullOrWhiteSpace(p.Filled)
                    ? new List<NdAnalysisPromptVersion>()
                    : MatchingVersions(versions.Where(v => v.PromptKey == p.Key), p.Filled!)))
                .ToList();
            if (candidates.All(c => c.Matches.Count == 0)) continue;

            // Several versions can share identical text (user block 1 is the same in v5-v9). The parts that
            // match exactly one version tell which set was in use; an ambiguous part takes that same version.
            var known = candidates.Where(c => c.Matches.Count == 1).Select(c => c.Matches[0].VersionNumber).ToList();
            foreach (var (_, matches) in candidates)
            {
                if (matches.Count == 0) continue;
                var hit = matches.Count == 1
                    ? matches[0]
                    : matches.Where(m => known.Contains(m.VersionNumber)).MaxBy(m => m.VersionNumber)
                        ?? PickByTime(matches, at);
                clause.PromptVersions.Add(new PromptVersionRef(hit.PromptKey, hit.VersionNumber, hit.Label));
                used.TryAdd((hit.PromptKey, hit.VersionNumber),
                    new PromptVersionSnapshot(hit.PromptKey, hit.VersionNumber, hit.Label, hit.PromptText));
            }
            break;
        }
    }

    /// <summary>Every stored version whose template could have produced <paramref name="filled"/>.</summary>
    public static List<NdAnalysisPromptVersion> MatchingVersions(IEnumerable<NdAnalysisPromptVersion> candidates, string filled)
    {
        var text = Norm(filled);
        return candidates.Where(v => TemplateMatches(Norm(v.PromptText), text)).ToList();
    }

    private static NdAnalysisPromptVersion PickByTime(List<NdAnalysisPromptVersion> matches, DateTimeOffset usedAt) =>
        matches.Where(v => v.CreatedAt <= usedAt.AddMinutes(1)).MaxBy(v => v.VersionNumber)
            ?? matches.MinBy(v => v.VersionNumber)!;

    private static readonly Regex Placeholder =
        new(@"\{(policy_context|clause_no|clause_text|clause_context)\}", RegexOptions.Compiled);

    /// <summary>
    /// The stored version whose template produced <paramref name="filled"/>: its literal pieces (the text
    /// between placeholders) must appear in order, the first at the start and the last at the end. When several
    /// versions share the same text, the newest one created before the call is taken.
    /// </summary>
    public static NdAnalysisPromptVersion? IdentifyVersion(
        IEnumerable<NdAnalysisPromptVersion> candidates, string filled, DateTimeOffset usedAt)
    {
        var matches = MatchingVersions(candidates, filled);
        return matches.Count == 0 ? null : PickByTime(matches, usedAt);
    }

    private static bool TemplateMatches(string template, string text)
    {
        var pieces = Placeholder.Split(template)
            .Where((_, i) => i % 2 == 0) // Split keeps captured placeholder names at odd indexes
            .ToList();
        if (pieces.Count == 1) return string.Equals(template, text, StringComparison.Ordinal);
        if (!text.StartsWith(pieces[0], StringComparison.Ordinal) || !text.EndsWith(pieces[^1], StringComparison.Ordinal))
            return false;
        var pos = pieces[0].Length;
        for (var i = 1; i < pieces.Count - 1; i++)
        {
            if (pieces[i].Length == 0) continue;
            var found = text.IndexOf(pieces[i], pos, StringComparison.Ordinal);
            if (found < 0) return false;
            pos = found + pieces[i].Length;
        }
        return pos <= text.Length - pieces[^1].Length;
    }

    private static string Norm(string s) => s.Replace("\r\n", "\n").Trim();

    // ---------------------------------------------------------------- clause evals

    /// <summary>
    /// Saves the chosen clauses of a run as clause evals: each becomes the next version for its clause
    /// (3.5 v1, v2, ...) and, when <paramref name="setCurrent"/>, the current version for that clause.
    /// Clauses without a saved AI result are skipped.
    /// </summary>
    public async Task<(List<NdClauseEval> Saved, List<string> Skipped)> SaveClausesAsync(
        Guid runId, IReadOnlyCollection<string> clauseNos, string? notes, bool setCurrent, Guid createdBy, CancellationToken ct)
    {
        var snap = await SnapshotRunAsync(runId, ct)
            ?? throw new InvalidOperationException("Analysis not found.");
        var wanted = clauseNos.Select(NumberKey).ToHashSet(StringComparer.Ordinal);
        var saved = new List<NdClauseEval>();
        var skipped = new List<string>();
        foreach (var clause in snap.Clauses.Where(c => wanted.Contains(NumberKey(c.ClauseNo))))
        {
            if (clause.OverallStatus == "")
            {
                skipped.Add(clause.ClauseNo);
                continue;
            }

            var key = ClauseKeyOf(clause);
            var existing = await db.NdClauseEvals.Where(e => e.ClauseKey == key).ToListAsync(ct);
            var makeCurrent = setCurrent || !existing.Any(e => e.IsCurrent);
            if (makeCurrent)
                foreach (var e in existing) e.IsCurrent = false;

            var prompts = snap.PromptVersions
                .Where(v => clause.PromptVersions.Any(r => r.PromptKey == v.PromptKey && r.VersionNumber == v.VersionNumber))
                .ToList();
            var eval = new NdClauseEval
            {
                ClauseNo = clause.ClauseNo,
                ClauseKey = key,
                ClauseTitle = clause.ClauseTitle,
                ClauseText = clause.ClauseText,
                RegulationDocumentId = clause.RegulationDocumentId,
                RegulationName = clause.RegulationName,
                VersionNumber = (existing.Count == 0 ? 0 : existing.Max(e => e.VersionNumber)) + 1,
                IsCurrent = makeCurrent,
                Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
                SourceRunId = runId,
                SourceRunName = snap.Run.Name,
                LlmProvider = clause.Provider ?? snap.Run.RegulLlmProvider,
                LlmModel = clause.Model ?? snap.Run.RegulLlmModel,
                PipelineVersion = clause.PipelineVersion,
                OverallStatus = clause.OverallStatus,
                GapCount = clause.Gaps.Count,
                PromptVersionsJson = JsonSerializer.Serialize(prompts, Json),
                ResultJson = JsonSerializer.Serialize(clause, Json),
                CreatedBy = createdBy,
                TenantId = snap.Run.TenantId,
            };
            db.NdClauseEvals.Add(eval);
            saved.Add(eval);
        }

        await db.SaveChangesAsync(ct);
        return (saved, skipped);
    }

    public async Task<bool> SetCurrentAsync(Guid evalId, CancellationToken ct)
    {
        var eval = await db.NdClauseEvals.FirstOrDefaultAsync(e => e.Id == evalId, ct);
        if (eval == null) return false;
        foreach (var e in await db.NdClauseEvals.Where(e => e.ClauseKey == eval.ClauseKey && e.IsCurrent).ToListAsync(ct))
            e.IsCurrent = false;
        eval.IsCurrent = true;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Deletes one version; when it was current, the newest remaining version becomes current.</summary>
    public async Task<bool> DeleteAsync(Guid evalId, CancellationToken ct)
    {
        var eval = await db.NdClauseEvals.FirstOrDefaultAsync(e => e.Id == evalId, ct);
        if (eval == null) return false;
        db.NdClauseEvals.Remove(eval);
        if (eval.IsCurrent)
        {
            var next = await db.NdClauseEvals
                .Where(e => e.ClauseKey == eval.ClauseKey && e.Id != eval.Id)
                .OrderByDescending(e => e.VersionNumber)
                .FirstOrDefaultAsync(ct);
            if (next != null) next.IsCurrent = true;
        }
        await db.SaveChangesAsync(ct);
        return true;
    }

    public static EvalClause ReadClause(NdClauseEval eval) =>
        JsonSerializer.Deserialize<EvalClause>(eval.ResultJson, Json) ?? new EvalClause { ClauseNo = eval.ClauseNo };

    public static List<PromptVersionSnapshot> ReadPromptVersions(NdClauseEval eval) =>
        JsonSerializer.Deserialize<List<PromptVersionSnapshot>>(eval.PromptVersionsJson, Json) ?? [];

    /// <summary>
    /// One-time fill for analyses judged before runs recorded their setup (idempotent, runs at startup). Runs
    /// judged since then record it themselves, so any finished run still missing it is an earlier one. Only
    /// runs that recorded an AI model, so demo runs (which never call an AI) are left untouched.
    /// Pipeline: everything before pipeline versions existed ran v1. Prompts: read from the run's AI call log
    /// when it has one, otherwise the versions current when the run was created (each seeded version was
    /// made current when it was added).
    /// </summary>
    public async Task<int> BackfillRunSetupAsync(CancellationToken ct)
    {
        var runs = await db.NdAnalysisRuns
            .Where(r => r.RegulLlmModel != null
                && r.Status != "running"
                && (r.RegulPipelineVersion == null || r.RegulPromptVersions == null || r.RegulPromptVersions.Contains("user 1")))
            .ToListAsync(ct);
        // Earlier fills could record a mixed set such as "system v8, user 1 v9, user 2 v8" (user block 1 has the
        // same text in v5-v9); those are read again from the AI call log with the corrected matching.
        foreach (var r in runs.Where(r => r.RegulPromptVersions != null && !IsSingleVersionSet(r.RegulPromptVersions)))
            r.RegulPromptVersions = null;
        runs = runs.Where(r => r.RegulPipelineVersion == null || r.RegulPromptVersions == null).ToList();
        if (runs.Count == 0) return 0;

        var versions = await db.NdAnalysisPromptVersions.AsNoTracking()
            .Where(v => v.PromptKey.StartsWith("regul_judgment_"))
            .ToListAsync(ct);
        var runIdsWithLog = (await db.NdRegulClauseTraces.AsNoTracking()
                .Select(t => t.AnalysisRunId).Distinct().ToListAsync(ct))
            .ToHashSet();

        var filled = 0;
        foreach (var run in runs)
        {
            try
            {
                run.RegulPipelineVersion ??= NdRegulPipelineVersions.V1;
                if (run.RegulPromptVersions == null && AnalysisWorkflowEngine.IsRegulFamily(run.WorkflowEngine))
                {
                    if (runIdsWithLog.Contains(run.Id))
                    {
                        var snap = await SnapshotRunAsync(run.Id, ct);
                        run.RegulPromptVersions = snap == null
                            ? null
                            : DescribePromptVersions(snap.Clauses.SelectMany(c => c.PromptVersions));
                    }

                    if (run.RegulPromptVersions == null)
                    {
                        var prefix = AnalysisWorkflowEngine.IsRegulPipelineFull(run.WorkflowEngine) ? "regul_judgment_full_" : "regul_judgment_";
                        var inUse = new[] { "system", "user_context", "user_query" }
                            .Select(part => PromptVersionAt(versions, prefix + part, run.CreatedAt))
                            .Where(v => v != null)
                            .Select(v => new PromptVersionRef(v!.PromptKey, v.VersionNumber, v.Label));
                        run.RegulPromptVersions = DescribePromptVersions(inUse);
                    }
                }

                await db.SaveChangesAsync(ct);
                filled++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not record the setup of analysis run {RunId}.", run.Id);
                db.ChangeTracker.Clear();
            }
        }

        return filled;
    }

    /// <summary>The version of a prompt that was current at <paramref name="at"/>; a run older than the
    /// version history itself ran the first (base) version.</summary>
    private static NdAnalysisPromptVersion? PromptVersionAt(
        IReadOnlyList<NdAnalysisPromptVersion> versions, string key, DateTimeOffset at) =>
        versions.Where(v => v.PromptKey == key && v.CreatedAt <= at).MaxBy(v => v.VersionNumber)
            ?? versions.Where(v => v.PromptKey == key).MinBy(v => v.VersionNumber);

    /// <summary>True when every part in "system v8, user 1 v8, user 2 v8" has the same version number.</summary>
    public static bool IsSingleVersionSet(string description) =>
        Regex.Matches(description, @"v(\d+)").Select(m => m.Groups[1].Value).Distinct().Count() <= 1;

    /// <summary>Run-level setup for display: AI model(s), pipeline version(s), prompt versions actually used.</summary>
    public sealed record RunSetup(string? LlmModel, string? PipelineVersions, string? PromptVersions);

    public static RunSetup DescribeSetup(RunSnapshot snap)
    {
        var models = snap.Clauses.Where(c => c.Model != null).Select(c => c.Model!).Distinct().ToList();
        return new RunSetup(
            models.Count > 0 ? string.Join(", ", models) : snap.Run.RegulLlmModel,
            DescribePipelines(snap.Clauses)
                ?? (snap.Run.RegulPipelineVersion is int v ? $"v{v}" : null),
            DescribePromptVersions(snap.Clauses.SelectMany(c => c.PromptVersions)) ?? snap.Run.RegulPromptVersions);
    }

    // ---------------------------------------------------------------- compare

    public sealed record ClauseComparison(
        string ClauseNo,
        string ClauseTitle,
        bool InEval,
        bool InRun,
        string? EvalStatus,
        string? RunStatus,
        bool StatusMatch,
        double? EvalConfidence,
        double? RunConfidence,
        int EvalGapCount,
        int RunGapCount,
        double? GapOverlapPct,
        List<string> EvalGaps,
        List<string> RunGaps,
        List<string> EvalActions,
        List<string> RunActions,
        string EvalPrompts,
        string RunPrompts,
        string? EvalModel,
        string? RunModel,
        int? EvalPipelineVersion,
        int? RunPipelineVersion,
        bool RunClauseContextSent,
        string Change,
        Guid? EvalId = null,
        int? EvalVersion = null);

    public sealed record ComparisonSummary(
        int ClausesCompared,
        int StatusMatches,
        double StatusAgreementPct,
        int GapCountMatches,
        double GapCountAgreementPct,
        double? AvgGapOverlapPct,
        int EvalGapTotal,
        int RunGapTotal,
        int OnlyInEval,
        int OnlyInRun,
        int MoreCompliant,
        int LessCompliant,
        int Changed,
        int Unchanged);

    /// <summary>One eval side (with its eval id and version) paired with one analysis side; either may be missing.</summary>
    public sealed record ComparePair(EvalClause? Eval, EvalClause? Run, Guid? EvalId = null, int? EvalVersion = null);

    /// <summary>Pairs two clause lists by clause number (used when both sides are plain lists).</summary>
    public static (ComparisonSummary Summary, List<ClauseComparison> Clauses) Compare(
        IReadOnlyList<EvalClause> evalClauses, IReadOnlyList<EvalClause> runClauses)
    {
        var evalBy = evalClauses.GroupBy(c => NumberKey(c.ClauseNo)).ToDictionary(g => g.Key, g => g.First());
        var runBy = runClauses.GroupBy(c => NumberKey(c.ClauseNo)).ToDictionary(g => g.Key, g => g.First());
        var pairs = evalBy.Keys.Union(runBy.Keys)
            .Select(k => new ComparePair(evalBy.GetValueOrDefault(k), runBy.GetValueOrDefault(k)))
            .ToList();
        return Compare(pairs);
    }

    /// <summary>Local, rule-based comparison (no AI): verdicts directly, gap wording by word overlap.</summary>
    public static (ComparisonSummary Summary, List<ClauseComparison> Clauses) Compare(IReadOnlyList<ComparePair> pairs)
    {
        var rows = new List<ClauseComparison>();
        foreach (var pair in pairs.OrderBy(p => (p.Eval ?? p.Run)!.ClauseNo, ClauseNoComparer.Instance))
        {
            var e = pair.Eval;
            var r = pair.Run;
            var both = e != null && r != null;
            var statusMatch = both && e!.OverallStatus == r!.OverallStatus;
            double? overlap = both ? GapOverlap(e!.Gaps, r!.Gaps) : null;
            string change;
            if (e == null) change = "new_in_run";
            else if (r == null) change = "missing_in_run";
            else if (r.OverallStatus == "") change = "not_judged";
            else if (!statusMatch)
                change = Rank(r.OverallStatus) > Rank(e.OverallStatus) ? "more_compliant" : "less_compliant";
            else if (e.Gaps.Count != r.Gaps.Count || (overlap ?? 100) < 60) change = "gaps_changed";
            else change = "same";

            rows.Add(new ClauseComparison(
                (e ?? r)!.ClauseNo,
                (e?.ClauseTitle is { Length: > 0 } ? e.ClauseTitle : r?.ClauseTitle) ?? "",
                e != null, r != null,
                e?.OverallStatus, r?.OverallStatus, statusMatch,
                e?.Confidence, r?.Confidence,
                e?.Gaps.Count ?? 0, r?.Gaps.Count ?? 0,
                overlap,
                e?.Gaps ?? [], r?.Gaps ?? [],
                e?.Actions ?? [], r?.Actions ?? [],
                DescribePrompts(e), DescribePrompts(r),
                e?.Model, r?.Model,
                e?.PipelineVersion, r?.PipelineVersion,
                r?.ClauseContextSent ?? false,
                change,
                pair.EvalId,
                pair.EvalVersion));
        }

        var compared = rows.Where(x => x.InEval && x.InRun).ToList();
        var overlaps = compared.Where(x => x.GapOverlapPct.HasValue).Select(x => x.GapOverlapPct!.Value).ToList();
        var summary = new ComparisonSummary(
            compared.Count,
            compared.Count(x => x.StatusMatch),
            Pct(compared.Count(x => x.StatusMatch), compared.Count),
            compared.Count(x => x.EvalGapCount == x.RunGapCount),
            Pct(compared.Count(x => x.EvalGapCount == x.RunGapCount), compared.Count),
            overlaps.Count > 0 ? Math.Round(overlaps.Average(), 1) : null,
            compared.Sum(x => x.EvalGapCount),
            compared.Sum(x => x.RunGapCount),
            rows.Count(x => x.InEval && !x.InRun),
            rows.Count(x => !x.InEval && x.InRun),
            compared.Count(x => x.Change == "more_compliant"),
            compared.Count(x => x.Change == "less_compliant"),
            compared.Count(x => x.Change == "gaps_changed"),
            compared.Count(x => x.Change == "same"));
        return (summary, rows);
    }

    private static double Pct(int n, int of) => of == 0 ? 0 : Math.Round(100.0 * n / of, 1);

    private static int Rank(string status) => status switch
    {
        "compliant" => 2,
        "partial" => 1,
        _ => 0,
    };

    private static string DescribePrompts(EvalClause? c) => c == null ? "" : DescribePromptVersions(c.PromptVersions) ?? "";

    /// <summary>"system v9, user 1 v9, user 2 v9"; null when nothing is known.</summary>
    public static string? DescribePromptVersions(IEnumerable<PromptVersionRef> versions)
    {
        var list = versions
            .DistinctBy(v => (v.PromptKey, v.VersionNumber))
            .OrderBy(v => PromptOrder(v.PromptKey)).ThenBy(v => v.VersionNumber)
            .Select(v => $"{ShortKey(v.PromptKey)} v{v.VersionNumber}")
            .ToList();
        return list.Count == 0 ? null : string.Join(", ", list);
    }

    private static int PromptOrder(string key) =>
        key.EndsWith("_system", StringComparison.Ordinal) ? 0
        : key.EndsWith("_user_context", StringComparison.Ordinal) ? 1
        : 2;

    private static string ShortKey(string key) => key
        .Replace("regul_judgment_full_", "full ", StringComparison.Ordinal)
        .Replace("regul_judgment_", "", StringComparison.Ordinal)
        .Replace("user_context", "user 1", StringComparison.Ordinal)
        .Replace("user_query", "user 2", StringComparison.Ordinal);

    /// <summary>
    /// How much the two gap lists say the same thing, 0-100: every gap is paired with its most similar gap on
    /// the other side (word overlap), averaged over both sides so missing and extra gaps both lower the score.
    /// Two empty lists are 100 (both found no gap).
    /// </summary>
    public static double GapOverlap(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count == 0 && b.Count == 0) return 100;
        if (a.Count == 0 || b.Count == 0) return 0;
        var ta = a.Select(Tokens).ToList();
        var tb = b.Select(Tokens).ToList();
        var forward = ta.Average(x => tb.Max(y => Jaccard(x, y)));
        var backward = tb.Average(y => ta.Max(x => Jaccard(x, y)));
        return Math.Round(100 * (forward + backward) / 2, 1);
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "that", "with", "this", "are", "not", "any", "all", "its", "their", "from", "into",
        "which", "such", "shall", "should", "must", "does", "missing", "policy", "excerpts", "lack", "lacks",
        "has", "have", "been", "being", "other", "also", "under", "where", "when", "than", "there", "these",
    };

    private static HashSet<string> Tokens(string s) =>
        Regex.Matches(s.ToLowerInvariant(), "[a-z0-9]+")
            .Select(m => m.Value)
            .Where(w => w.Length >= 3 && !StopWords.Contains(w))
            .Select(w => w.Length > 5 ? w[..5] : w) // crude stemming: "definition"/"defined" -> "defin"
            .ToHashSet(StringComparer.Ordinal);

    private static double Jaccard(HashSet<string> x, HashSet<string> y)
    {
        if (x.Count == 0 && y.Count == 0) return 1;
        var inter = x.Count(y.Contains);
        var union = x.Count + y.Count - inter;
        return union == 0 ? 0 : (double)inter / union;
    }

    // ---------------------------------------------------------------- helpers

    public static string NormalizeStatus(string? status)
    {
        var s = (status ?? "").Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
        return s switch
        {
            "compliant" or "fully_compliant" => "compliant",
            "partial" or "partially_compliant" => "partial",
            "non_compliant" or "noncompliant" or "not_compliant" => "non_compliant",
            _ => s,
        };
    }

    private static readonly Regex ItemMarker = new(@"(?:^|\s)\[(\d+)\]\s*", RegexOptions.Compiled);

    /// <summary>"[1] a [2] b" (one per line or inline) -> ["a", "b"]; "N/A"/empty -> []; unnumbered text -> [text].
    /// Several action lines with the same [n] stay separate items.</summary>
    public static List<string> NumberedItems(string? text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0 || t.Equals("N/A", StringComparison.OrdinalIgnoreCase) || t.Equals("None", StringComparison.OrdinalIgnoreCase))
            return [];
        var marks = ItemMarker.Matches(t);
        if (marks.Count == 0) return [t];
        var items = new List<string>();
        for (var i = 0; i < marks.Count; i++)
        {
            var start = marks[i].Index + marks[i].Length;
            var end = i + 1 < marks.Count ? marks[i + 1].Index : t.Length;
            var item = t[start..end].Trim();
            if (item.Length > 0) items.Add($"[{marks[i].Groups[1].Value}] {item}");
        }
        return items;
    }

    public static string NumberKey(string clauseNo) => NdRegulClauseContextService.NormalizeNumber(clauseNo) ?? clauseNo.Trim();

    /// <summary>A clause's identity across analyses: its regulation document (or name) plus its number.</summary>
    public static string ClauseKeyOf(EvalClause c) =>
        $"{(c.RegulationDocumentId?.ToString() ?? (c.RegulationName ?? "").Trim().ToLowerInvariant())}|{NumberKey(c.ClauseNo)}";

    public sealed class ClauseNoComparer : IComparer<string>
    {
        public static readonly ClauseNoComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            var a = (x ?? "").Trim().TrimEnd('.').Split('.');
            var b = (y ?? "").Trim().TrimEnd('.').Split('.');
            for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
            {
                var c = long.TryParse(a[i], out var p) && long.TryParse(b[i], out var q)
                    ? p.CompareTo(q)
                    : string.Compare(a[i], b[i], StringComparison.Ordinal);
                if (c != 0) return c;
            }
            return a.Length.CompareTo(b.Length);
        }
    }
}
