using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Services.Llm;
using Reguliq.Api.Services.LocalDocs;
using Reguliq.Api.Services.Storage;
using Reguliq.Api.Services.NewDashboard.Ai;

namespace Reguliq.Api.Services.NewDashboard;

/// <summary>
/// Re-checks the open gaps of a finished analysis against gap evidence documents uploaded afterwards,
/// through the same stages as a new analysis: prepare the documents (Azure parse, structural chunking,
/// index), retrieve the relevant evidence sections per clause, then ask the Regul workflow model to judge
/// each open gap and corrective action against them.
///
/// The clause's original judgment and gap list are never rewritten, so every gap stays on record. Each
/// verdict is saved as a <see cref="NdGapEvidenceReview"/>; action plans the evidence satisfies are
/// resolved, partly satisfied ones are split into a resolved part and a still-pending part, and gap and
/// clause status follow through <see cref="NdGapStatusResolver"/>.
/// </summary>
public sealed class NdGapEvidenceRerunService(
    AppDbContext db,
    NdGapEvidencePrepareService prepare,
    RegulEmbeddingRetrievalService retrieval,
    RegulWorkflowLlmSettingsService llmSettings,
    RegulWorkflowLlmService regulLlm,
    NdRegulAnalysisProcessor analysisProcessor,
    SupabaseStorageService storage,
    IServiceScopeFactory scopeFactory,
    ILogger<NdGapEvidenceRerunService> logger)
{
    public sealed record GapInput(int Index, string? Text);

    public sealed record PointInput(Guid PointId, int? GapIndex, List<GapInput>? Gaps);

    public sealed record StartResult(NdGapEvidenceRerun? Rerun, bool AlreadyRunning, string? Error);

    private const int MaxContextSections = 20;
    private const int MaxWholeDocumentSections = 40;
    private const int MaxSectionChars = 2500;
    private const int MaxContextChars = 40_000;
    private const int MaxGapTextChars = 2000;

    private static readonly int JudgmentConcurrency = Math.Max(
        1,
        int.TryParse(Environment.GetEnvironmentVariable("BCP_GAP_EVIDENCE_CONCURRENCY"), out var configured)
            ? configured
            : 3);

    /// <summary>Reruns executing in this API process — anything "running" in the database but missing
    /// here was cut off by a restart.</summary>
    private static readonly ConcurrentDictionary<Guid, byte> RunningInProcess = new();

    /// <summary>Word layout of each evidence .docx, so a quote can cite the page it is actually on.</summary>
    private readonly Dictionary<Guid, DocxRenderedPages.PagedText> wordLayouts = new();

    private static readonly JsonSerializerOptions StoreJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ------------------------------------------------------------------ start

    /// <summary>
    /// Creates the rerun and one queued review per clause. Returns the active rerun instead when one is
    /// already in progress for this analysis, so the page attaches to it rather than starting a second.
    /// </summary>
    public async Task<StartResult> CreateAsync(
        NdAnalysisRun run,
        IReadOnlyList<PointInput> requested,
        string scope,
        Guid? createdBy,
        CancellationToken ct)
    {
        var active = await FindActiveAsync(run.Id, ct);
        if (active != null) return new StartResult(active, true, null);

        var requestedIds = requested.Select(p => p.PointId).Distinct().ToList();
        var points = await db.NdAnalysisPoints.AsNoTracking()
            .Where(p => p.AnalysisRunId == run.Id && requestedIds.Contains(p.Id))
            .ToListAsync(ct);
        if (points.Count == 0)
            return new StartResult(null, false, "None of the selected clauses belong to this analysis.");

        var attachments = await db.NdAnalysisPointAttachments.AsNoTracking()
            .Where(a => requestedIds.Contains(a.AnalysisPointId))
            .ToListAsync(ct);

        var docIds = attachments.Select(a => a.StoredDocumentId).Distinct().ToList();
        var docNames = await db.StoredDocuments.AsNoTracking()
            .Where(d => docIds.Contains(d.Id))
            .ToDictionaryAsync(d => d.Id, d => d.Title ?? d.OriginalFileName ?? "document", ct);

        var findings = await db.NdRegulForwardFindings.AsNoTracking()
            .Where(f => f.AnalysisRunId == run.Id && f.AnalysisPointId != null && requestedIds.Contains(f.AnalysisPointId.Value))
            .ToListAsync(ct);

        var rerun = new NdGapEvidenceRerun
        {
            AnalysisRunId = run.Id,
            TenantId = run.TenantId,
            Scope = scope,
            Status = GapEvidenceStatuses.Queued,
            Phase = GapEvidencePhases.Queued,
            CreatedBy = createdBy,
        };

        var reviews = new List<NdGapEvidenceReview>();
        var usedDocIds = new HashSet<Guid>();
        foreach (var input in requested.DistinctBy(p => p.PointId))
        {
            var point = points.FirstOrDefault(p => p.Id == input.PointId);
            if (point == null) continue;

            var pointDocs = attachments
                .Where(a => a.AnalysisPointId == point.Id
                    && (input.GapIndex == null || a.ActionIndex == null || a.ActionIndex == input.GapIndex))
                .Select(a => a.StoredDocumentId)
                .Distinct()
                .ToList();
            if (pointDocs.Count == 0) continue;

            var finding = findings.FirstOrDefault(f => f.AnalysisPointId == point.Id);
            var gaps = SanitizeGaps(input.Gaps, input.GapIndex);
            foreach (var id in pointDocs) usedDocIds.Add(id);

            reviews.Add(new NdGapEvidenceReview
            {
                RerunId = rerun.Id,
                AnalysisRunId = run.Id,
                AnalysisPointId = point.Id,
                TenantId = run.TenantId,
                GapIndexFilter = input.GapIndex,
                Status = GapEvidenceStatuses.Queued,
                ClauseNo = ResolveClauseNo(point, finding),
                PriorFinalStatus = point.FinalStatus,
                EvidenceDocumentsJson = Serialize(pointDocs.Select(id => new EvidenceDocRef(id, docNames.GetValueOrDefault(id, "document")))),
                GapsJson = Serialize(gaps.Select(g => new StoredGap { Index = g.Index, Text = g.Text })),
            });
        }

        if (reviews.Count == 0)
            return new StartResult(null, false,
                "Upload a gap analysis document first — none of the selected clauses has an evidence document attached.");

        rerun.TotalPoints = reviews.Count;
        rerun.EvidenceDocumentsJson = Serialize(usedDocIds.Select(id => new EvidenceDocRef(id, docNames.GetValueOrDefault(id, "document"))));

        db.NdGapEvidenceReruns.Add(rerun);
        db.NdGapEvidenceReviews.AddRange(reviews);
        await db.SaveChangesAsync(ct);
        return new StartResult(rerun, false, null);
    }

    /// <summary>Runs the rerun on its own DI scope, billed to the analysis' workspace.</summary>
    public void StartInBackground(Guid rerunId, Guid? tenantId, Guid runId, Guid? userId)
    {
        RunningInProcess[rerunId] = 0;
        _ = Task.Run(async () =>
        {
            using var billing = NdAiUsageContext.Enter(tenantId, runId, "gap_evidence_rerun", userId);
            try
            {
                using var scope = scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<NdGapEvidenceRerunService>();
                await service.ExecuteAsync(rerunId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Gap evidence rerun {RerunId} crashed", rerunId);
            }
            finally
            {
                RunningInProcess.TryRemove(rerunId, out _);
            }
        }, CancellationToken.None);
    }

    public async Task<NdGapEvidenceRerun?> FindActiveAsync(Guid runId, CancellationToken ct)
    {
        var active = await db.NdGapEvidenceReruns
            .Where(r => r.AnalysisRunId == runId
                && (r.Status == GapEvidenceStatuses.Queued || r.Status == GapEvidenceStatuses.Running))
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (active == null) return null;
        if (!await MarkInterruptedIfStaleAsync(active, ct)) return active;
        return null;
    }

    /// <summary>A rerun the server stopped tracking (restart, crash) is closed out as failed so the
    /// page stops waiting on it and the user can start again.</summary>
    public async Task<bool> MarkInterruptedIfStaleAsync(NdGapEvidenceRerun rerun, CancellationToken ct)
    {
        if (!GapEvidenceStatuses.IsActive(rerun.Status)) return false;
        var idle = DateTimeOffset.UtcNow - rerun.UpdatedAt;
        var orphaned = !RunningInProcess.ContainsKey(rerun.Id) && idle > TimeSpan.FromMinutes(3);
        if (!orphaned && idle < TimeSpan.FromMinutes(25)) return false;

        rerun.Status = GapEvidenceStatuses.Failed;
        rerun.Phase = GapEvidencePhases.Done;
        rerun.Error = "The re-check was interrupted before it finished (the server restarted). Run it again.";
        rerun.FinishedAt = DateTimeOffset.UtcNow;
        rerun.UpdatedAt = DateTimeOffset.UtcNow;
        var open = await db.NdGapEvidenceReviews
            .Where(r => r.RerunId == rerun.Id
                && (r.Status == GapEvidenceStatuses.Queued || r.Status == GapEvidenceStatuses.Running))
            .ToListAsync(ct);
        foreach (var review in open)
        {
            review.Status = GapEvidenceStatuses.Failed;
            review.Error = "Interrupted before this clause was checked.";
            review.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ---------------------------------------------------------------- execute

    internal sealed class ClauseWork
    {
        public required NdGapEvidenceReview Review { get; init; }
        public required NdAnalysisPoint Point { get; init; }
        public NdRegulForwardFinding? Finding { get; init; }
        public required string ClauseNo { get; init; }
        public required string ClauseText { get; init; }
        public List<StoredGap> Gaps { get; set; } = [];
        public List<ActionRef> Actions { get; set; } = [];
        public List<ContextSection> Sections { get; set; } = [];
        public List<EvidenceDocRef> Docs { get; set; } = [];
        public RegulJudgmentResult? PriorJudgment { get; init; }
        /// <summary>New Analysis judgment for this clause over the original policies plus the evidence.</summary>
        public NdRegulAnalysisProcessor.ForwardJudgmentPrep? AnalysisPrep { get; set; }
        public RegulJudgmentResult? Reanalysis { get; set; }
        public string Prompt { get; set; } = "";
        /// <summary>Evidence-check LLM calls (request + raw response), saved with the clause's outcome.</summary>
        public List<NdRegulClauseTrace> EvidenceTraces { get; } = [];

        public IEnumerable<NdRegulClauseTrace> AllTraces() =>
            NdRegulAnalysisProcessor.WithSource(
                (AnalysisPrep?.Traces ?? []).Concat(EvidenceTraces).ToList(),
                RegulClauseTraceSources.GapEvidence);
    }

    public async Task ExecuteAsync(Guid rerunId, CancellationToken ct)
    {
        var rerun = await db.NdGapEvidenceReruns.FirstOrDefaultAsync(r => r.Id == rerunId, ct);
        if (rerun == null) return;
        var run = await db.NdAnalysisRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == rerun.AnalysisRunId, ct);
        var reviews = await db.NdGapEvidenceReviews.Where(r => r.RerunId == rerunId).ToListAsync(ct);
        if (run == null || reviews.Count == 0)
        {
            await FailRerunAsync(rerun, reviews, "Analysis or clauses no longer exist.", ct);
            return;
        }

        try
        {
            var cfg = await llmSettings.GetConfigAsync(ct);
            rerun.LlmProvider = cfg.Provider;
            rerun.LlmModel = cfg.Model;
            rerun.Status = GapEvidenceStatuses.Running;
            rerun.StartedAt = DateTimeOffset.UtcNow;
            await SetPhaseAsync(rerun, GapEvidencePhases.Parsing, "Preparing evidence documents", ct);

            var preparedDocs = await PrepareEvidenceDocsAsync(rerun, run.WorkflowEngine, ct);

            await SetPhaseAsync(rerun, GapEvidencePhases.Retrieval, "Retrieving relevant evidence sections", ct);
            var works = await BuildClauseWorkAsync(rerun, run, reviews, preparedDocs, ct);

            await SetPhaseAsync(rerun, GapEvidencePhases.Forward, "Judging open gaps against the evidence", ct);
            await JudgeAndApplyAsync(rerun, works, cfg, ct);

            rerun.Status = rerun.CompletedPoints == 0 && rerun.FailedPoints > 0
                ? GapEvidenceStatuses.Failed
                : GapEvidenceStatuses.Completed;
            if (rerun.Status == GapEvidenceStatuses.Failed && string.IsNullOrWhiteSpace(rerun.Error))
                rerun.Error = reviews.Select(r => r.Error).FirstOrDefault(e => !string.IsNullOrWhiteSpace(e))
                    ?? "No clause could be checked.";
            rerun.Phase = GapEvidencePhases.Done;
            rerun.PhaseDetail = null;
            rerun.FinishedAt = DateTimeOffset.UtcNow;
            rerun.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);

            logger.LogInformation(
                "Gap evidence rerun {RerunId} on run {RunId} finished: {Done}/{Total} clause(s), {Failed} failed, {Fulfilled} gap(s) fulfilled, {Partial} partial, {Resolved} action(s) resolved, {Split} split",
                rerun.Id, run.Id, rerun.CompletedPoints, rerun.TotalPoints, rerun.FailedPoints,
                rerun.FulfilledGaps, rerun.PartialGaps, rerun.ResolvedActions, rerun.SplitActions);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Gap evidence rerun {RerunId} failed", rerunId);
            await FailRerunAsync(rerun, reviews, ex.GetBaseException().Message, CancellationToken.None);
        }
    }

    private async Task FailRerunAsync(
        NdGapEvidenceRerun rerun,
        IEnumerable<NdGapEvidenceReview> reviews,
        string message,
        CancellationToken ct)
    {
        foreach (var review in reviews.Where(r => GapEvidenceStatuses.IsActive(r.Status)))
        {
            review.Status = GapEvidenceStatuses.Failed;
            review.Error ??= message;
            review.UpdatedAt = DateTimeOffset.UtcNow;
            rerun.FailedPoints++;
        }
        rerun.Status = GapEvidenceStatuses.Failed;
        rerun.Phase = GapEvidencePhases.Done;
        rerun.PhaseDetail = null;
        rerun.Error = message;
        rerun.FinishedAt = DateTimeOffset.UtcNow;
        rerun.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private async Task SetPhaseAsync(NdGapEvidenceRerun rerun, string phase, string? detail, CancellationToken ct)
    {
        rerun.Phase = phase;
        rerun.PhaseDetail = detail;
        rerun.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Parse, structurally chunk and index every evidence document once for the whole rerun.
    /// Returns the documents that are ready, with the failure reason for any that are not.</summary>
    private async Task<Dictionary<Guid, string?>> PrepareEvidenceDocsAsync(
        NdGapEvidenceRerun rerun,
        string? workflowEngine,
        CancellationToken ct)
    {
        var docs = Deserialize<List<EvidenceDocRef>>(rerun.EvidenceDocumentsJson) ?? [];
        var result = new Dictionary<Guid, string?>();
        var index = 0;
        foreach (var doc in docs)
        {
            index++;
            try
            {
                await prepare.PrepareDocumentsAsync(
                    [doc.Id],
                    workflowEngine,
                    ct,
                    async step =>
                    {
                        rerun.PhaseDetail = docs.Count > 1 ? $"{step} ({index}/{docs.Count})" : step;
                        rerun.UpdatedAt = DateTimeOffset.UtcNow;
                        await db.SaveChangesAsync(ct);
                    });
                result[doc.Id] = null;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Gap evidence document {DocId} could not be prepared for rerun {RerunId}", doc.Id, rerun.Id);
                result[doc.Id] = ex.Message;
            }
        }

        await LoadWordLayoutsAsync(result.Where(r => r.Value == null).Select(r => r.Key).ToList(), ct);

        if (result.Count > 0 && result.Values.All(e => e != null))
            throw new InvalidOperationException(
                $"The evidence document could not be prepared: {result.Values.First()}");
        return result;
    }

    private async Task<List<ClauseWork>> BuildClauseWorkAsync(
        NdGapEvidenceRerun rerun,
        NdAnalysisRun run,
        List<NdGapEvidenceReview> reviews,
        Dictionary<Guid, string?> preparedDocs,
        CancellationToken ct)
    {
        var pointIds = reviews.Select(r => r.AnalysisPointId).ToList();
        var points = await db.NdAnalysisPoints.AsNoTracking().Where(p => pointIds.Contains(p.Id)).ToListAsync(ct);
        var findings = await db.NdRegulForwardFindings.AsNoTracking()
            .Where(f => f.AnalysisRunId == run.Id && f.AnalysisPointId != null && pointIds.Contains(f.AnalysisPointId.Value))
            .ToListAsync(ct);
        var gapRows = await db.NdAnalysisGaps.AsNoTracking()
            .Where(g => pointIds.Contains(g.AnalysisPointId))
            .ToListAsync(ct);
        var plans = await db.NdAnalysisActionPlans.AsNoTracking()
            .Where(p => pointIds.Contains(p.AnalysisPointId))
            .OrderBy(p => p.GapIndex).ThenBy(p => p.SortOrder).ThenBy(p => p.CreatedAt)
            .ToListAsync(ct);

        var works = new List<ClauseWork>();
        var position = 0;
        foreach (var review in reviews)
        {
            position++;
            var point = points.FirstOrDefault(p => p.Id == review.AnalysisPointId);
            if (point == null)
            {
                await FailReviewAsync(rerun, review, "Clause no longer exists.", ct);
                continue;
            }

            var finding = findings.FirstOrDefault(f => f.AnalysisPointId == point.Id);
            var prior = TryParseJudgment(finding?.ResultJson);
            var (clauseNo, clauseText) = ResolveClause(point, finding);

            var docs = (Deserialize<List<EvidenceDocRef>>(review.EvidenceDocumentsJson) ?? [])
                .Where(d => preparedDocs.TryGetValue(d.Id, out var err) && err == null)
                .ToList();
            if (docs.Count == 0)
            {
                var reason = (Deserialize<List<EvidenceDocRef>>(review.EvidenceDocumentsJson) ?? [])
                    .Select(d => preparedDocs.GetValueOrDefault(d.Id))
                    .FirstOrDefault(e => !string.IsNullOrWhiteSpace(e));
                await FailReviewAsync(rerun, review, reason ?? "No prepared evidence document for this clause.", ct);
                continue;
            }

            var resolvedGapIndexes = gapRows
                .Where(g => g.AnalysisPointId == point.Id && g.Status == GapStatuses.Resolved)
                .Select(g => g.GapIndex)
                .ToHashSet();
            var pointPlans = plans.Where(p => p.AnalysisPointId == point.Id).ToList();

            var gaps = Deserialize<List<StoredGap>>(review.GapsJson) ?? [];
            if (gaps.Count == 0)
                gaps = FallbackGaps(prior, gapRows.Where(g => g.AnalysisPointId == point.Id), pointPlans, review.GapIndexFilter);
            gaps = gaps
                .Where(g => !resolvedGapIndexes.Contains(g.Index))
                .Where(g => review.GapIndexFilter == null || g.Index == review.GapIndexFilter)
                .ToList();
            if (gaps.Count == 0)
            {
                await FailReviewAsync(rerun, review, "Every gap on this clause is already resolved.", ct);
                continue;
            }

            var gapIndexes = gaps.Select(g => g.Index).ToHashSet();
            var actions = pointPlans
                .Where(p => p.Status != ActionPlanStatuses.Resolved && gapIndexes.Contains(EffectiveGapIndex(p.GapIndex)))
                .Select((p, i) => new ActionRef($"A{i + 1}", p.Id, EffectiveGapIndex(p.GapIndex), p.ActionPlan.Trim()))
                .Where(a => a.Text.Length > 0)
                .ToList();

            rerun.PhaseDetail = $"Retrieving evidence for §{clauseNo} ({position}/{reviews.Count})";
            rerun.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            if (finding == null)
            {
                await FailReviewAsync(rerun, review, "This clause has no analysis record to re-run.", ct);
                continue;
            }

            // Step 1 — the New Analysis judgment itself: same retrieval, same admin prompts, same
            // post-processing, over the run's internal documents plus the uploaded evidence.
            var (analysisPrep, retrievalPreview) = await analysisProcessor.PrepareClauseJudgmentWithExtraDocsAsync(
                run, finding, point, docs.Select(d => d.Id).ToList(), ct);
            review.RetrievalJson = retrievalPreview == null
                ? null
                : RegulEmbeddingRetrievalService.SerializePreview(retrievalPreview);

            // Step 2 context — the evidence itself. A short document is passed whole so no passage
            // can be missed; a long one goes through the same hybrid retrieval.
            var contextSections = await LoadEvidenceSectionsAsync(docs, MaxWholeDocumentSections, ct);
            if (contextSections.Count >= MaxWholeDocumentSections)
            {
                var queries = new List<string> { clauseText };
                queries.AddRange(gaps.Select(g => g.Text ?? ""));
                var sections = await retrieval.RetrieveFromDocumentsAsync(
                    docs.Select(d => d.Id).ToList(), queries, MaxContextSections, ct);
                if (sections.Count > 0)
                {
                    contextSections = sections.Select(s => new ContextSection
                    {
                        DocumentId = s.SourceDocumentId,
                        DocumentName = s.SourceDocumentName ?? docs.FirstOrDefault(d => d.Id == s.SourceDocumentId)?.Name ?? "document",
                        SectionRef = s.ClauseNo,
                        Page = s.SourcePage,
                        Score = s.Score,
                        Text = s.Text,
                    }).ToList();
                }
            }

            contextSections = TrimContext(contextSections);
            for (var i = 0; i < contextSections.Count; i++) contextSections[i].Label = $"S{i + 1}";

            review.ContextJson = Serialize(contextSections.Select(s => s.ForStorage()));
            review.EvidenceDocumentsJson = Serialize(docs);
            review.ClauseNo = clauseNo;
            review.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            if (contextSections.Count == 0)
            {
                await FailReviewAsync(rerun, review, "The evidence document has no readable text to compare against.", ct);
                continue;
            }

            var work = new ClauseWork
            {
                Review = review,
                Point = point,
                Finding = finding,
                ClauseNo = clauseNo,
                ClauseText = clauseText,
                Gaps = gaps,
                Actions = actions,
                Sections = contextSections,
                Docs = docs,
                PriorJudgment = prior,
                AnalysisPrep = analysisPrep,
            };
            works.Add(work);
        }

        return works;
    }

    private async Task JudgeAndApplyAsync(
        NdGapEvidenceRerun rerun,
        List<ClauseWork> works,
        DualVerifyLlmConfig cfg,
        CancellationToken ct)
    {
        // The model calls run in parallel; every database touch goes through dbGate because a
        // DbContext must never be used by two threads at once.
        using var llmGate = new SemaphoreSlim(JudgmentConcurrency);
        using var dbGate = new SemaphoreSlim(1, 1);

        var tasks = works.Select(async work =>
        {
            await llmGate.WaitAsync(ct);
            try
            {
                await dbGate.WaitAsync(ct);
                try
                {
                    work.Review.Status = GapEvidenceStatuses.Running;
                    work.Review.StartedAt = DateTimeOffset.UtcNow;
                    work.Review.UpdatedAt = DateTimeOffset.UtcNow;
                    rerun.PhaseDetail = $"Judging §{work.ClauseNo} against the evidence";
                    rerun.UpdatedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(ct);
                }
                finally
                {
                    dbGate.Release();
                }

                EvidenceJudgment? judgment = null;
                Exception? error = null;
                try
                {
                    try
                    {
                        work.Reanalysis = await analysisProcessor.ExecuteClauseJudgmentAsync(work.AnalysisPrep!, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // The evidence check itself can still run; it just goes without the fresh verdict.
                        logger.LogWarning(ex, "Re-analysis judgment failed for clause {ClauseNo} in rerun {RerunId}; continuing with the evidence check", work.ClauseNo, rerun.Id);
                        work.Reanalysis = null;
                    }
                    work.Prompt = BuildPrompt(work);
                    judgment = await CallModelAsync(work, cfg, ct);
                }
                catch (Exception ex)
                {
                    error = ex;
                }

                await dbGate.WaitAsync(CancellationToken.None);
                try
                {
                    // Saved by the outcome/failure SaveChanges below.
                    db.NdRegulClauseTraces.AddRange(work.AllTraces());
                    if (judgment != null)
                    {
                        try
                        {
                            await ApplyOutcomeAsync(rerun, work, judgment, ct);
                            return;
                        }
                        catch (Exception ex)
                        {
                            error = ex;
                        }
                    }

                    logger.LogWarning(error, "Gap evidence judgment failed for clause {ClauseNo} in rerun {RerunId}", work.ClauseNo, rerun.Id);
                    await FailReviewAsync(rerun, work.Review, error?.GetBaseException().Message ?? "The model returned no verdict.", CancellationToken.None);
                }
                finally
                {
                    dbGate.Release();
                }
            }
            finally
            {
                llmGate.Release();
            }
        });

        await Task.WhenAll(tasks);
    }

    private async Task FailReviewAsync(NdGapEvidenceRerun rerun, NdGapEvidenceReview review, string message, CancellationToken ct)
    {
        review.Status = GapEvidenceStatuses.Failed;
        review.Error = message;
        review.CompletedAt = DateTimeOffset.UtcNow;
        review.UpdatedAt = DateTimeOffset.UtcNow;
        rerun.FailedPoints++;
        rerun.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------------ model

    private async Task<EvidenceJudgment> CallModelAsync(ClauseWork work, DualVerifyLlmConfig cfg, CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var prompt = attempt == 0
                ? work.Prompt
                : work.Prompt + "\n\nYour previous reply was not valid JSON. Reply again with ONLY the JSON object described above.";
            var trace = new NdRegulClauseTrace
            {
                AnalysisRunId = work.Review.AnalysisRunId,
                FindingId = work.Finding?.Id,
                ClauseNo = work.ClauseNo,
                Step = RegulClauseTraceSteps.EvidenceCheck,
                Attempt = attempt + 1,
                Provider = cfg.Provider,
                Model = cfg.Model,
                QueryText = prompt,
                CharsSent = prompt.Length,
                Notes = $"evidence: {string.Join("; ", work.Docs.Select(d => d.Name))}; {work.Sections.Count} evidence section(s); {work.Gaps.Count} gap(s), {work.Actions.Count} action(s) checked",
                TenantId = work.Review.TenantId,
            };
            work.EvidenceTraces.Add(trace);
            var started = System.Diagnostics.Stopwatch.StartNew();
            string raw;
            try
            {
                raw = await regulLlm.AnalyzeTextWithConfigAsync(prompt, cfg, ct);
            }
            catch (Exception ex)
            {
                trace.DurationMs = (int)started.ElapsedMilliseconds;
                trace.Error = ex.Message;
                throw;
            }
            trace.DurationMs = (int)started.ElapsedMilliseconds;
            trace.ResponseText = raw;
            try
            {
                return NdRegulLlmJsonHelper.ParseJsonObject<EvidenceJudgment>(raw);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                trace.Error = $"Could not parse the response: {ex.Message}";
                last = ex;
            }
        }
        throw new InvalidOperationException($"The model did not return a readable verdict: {last?.Message}");
    }

    private static string BuildPrompt(ClauseWork work)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a regulatory compliance analyst. A bank previously failed (fully or partly) the regulatory clause below.");
        sb.AppendLine("It has now uploaded NEW evidence documents. Decide, gap by gap and action by action, whether the NEW evidence closes what was missing.");
        sb.AppendLine();
        sb.AppendLine($"REGULATORY CLAUSE §{work.ClauseNo}");
        sb.AppendLine(Truncate(work.ClauseText, 4000));
        sb.AppendLine();

        var prior = work.PriorJudgment;
        sb.AppendLine($"ORIGINAL ASSESSMENT (before the new evidence): {StatusLabel(work.Review.PriorFinalStatus ?? prior?.OverallStatus)}");
        if (prior != null && !string.IsNullOrWhiteSpace(prior.GapDescription) && prior.GapDescription.Trim() != "N/A")
            sb.AppendLine(Truncate(prior.GapDescription.Trim(), 2500));
        sb.AppendLine();

        sb.AppendLine("OPEN GAPS TO RE-CHECK");
        foreach (var gap in work.Gaps)
            sb.AppendLine($"[G{gap.Index}] {Truncate(gap.Text ?? "", MaxGapTextChars)}");
        sb.AppendLine();

        sb.AppendLine("OPEN CORRECTIVE ACTIONS");
        if (work.Actions.Count == 0)
            sb.AppendLine("None recorded. Return an empty \"actions\" array.");
        foreach (var action in work.Actions)
            sb.AppendLine($"[{action.Label}] (for G{action.GapIndex}) {Truncate(action.Text, 1500)}");
        sb.AppendLine();

        var fresh = work.Reanalysis;
        if (fresh != null)
        {
            sb.AppendLine("RE-ANALYSIS (the same analysis pipeline and prompts as the original run, now over the original policies PLUS the new evidence)");
            sb.AppendLine($"Status: {StatusLabel(fresh.OverallStatus)} (confidence {fresh.Confidence:0.##})");
            if (!string.IsNullOrWhiteSpace(fresh.Interpretation)) sb.AppendLine($"Interpretation: {Truncate(fresh.Interpretation.Trim(), 2000)}");
            if (!string.IsNullOrWhiteSpace(fresh.GapDescription) && fresh.GapDescription.Trim() != "N/A")
                sb.AppendLine($"Remaining gap: {Truncate(fresh.GapDescription.Trim(), 2500)}");
            if (!string.IsNullOrWhiteSpace(fresh.SuggestedAction) && fresh.SuggestedAction.Trim() != "N/A")
                sb.AppendLine($"Suggested action: {Truncate(fresh.SuggestedAction.Trim(), 1500)}");
            if (!string.IsNullOrWhiteSpace(fresh.DocumentReference)) sb.AppendLine($"Document reference: {fresh.DocumentReference.Trim()}");
            foreach (var q in fresh.PolicyExtract.Take(10)) sb.AppendLine($"Policy extract: \"{Truncate(q.Trim(), 600)}\"");
            sb.AppendLine();
        }

        sb.AppendLine($"NEW EVIDENCE — full text of: {string.Join("; ", work.Docs.Select(d => d.Name))}");
        foreach (var s in work.Sections)
        {
            var where = new List<string> { s.DocumentName };
            if (!string.IsNullOrWhiteSpace(s.SectionRef)) where.Add($"section {s.SectionRef}");
            if (s.Page.HasValue) where.Add($"p.{s.Page}");
            sb.AppendLine($"[{s.Label}] {string.Join(", ", where)}");
            sb.AppendLine(s.Text);
            sb.AppendLine();
        }

        sb.AppendLine("RULES");
        if (fresh != null)
            sb.AppendLine("- The RE-ANALYSIS is the authoritative judgment of the clause with the new evidence included. Map it onto each open gap and action: a gap the re-analysis no longer reports as missing is closed, a gap it still reports is not.");
        sb.AppendLine("- Credit only the NEW evidence sections above for anything newly covered; the original policies were already judged insufficient. Read every NEW evidence section — a relevant sentence can sit in any section, including introductions and annexes.");
        sb.AppendLine("- Gap outcome: \"fulfilled\" = the evidence fully supplies everything the gap says is missing; \"partially_fulfilled\" = it supplies some of it; \"not_fulfilled\" = it does not address it.");
        sb.AppendLine("- For every gap, \"covered\" says exactly what the new evidence now covers, and \"remaining\" says exactly what is still missing (empty only when fulfilled).");
        sb.AppendLine("- Support every fulfilled or partially_fulfilled gap with at least one quote copied VERBATIM (character for character) from a section, citing its [S#] label.");
        sb.AppendLine("- Action outcome: \"fulfilled\" = the evidence shows the action is already done in full; \"partially_fulfilled\" = part of it is done; \"not_fulfilled\" = not done.");
        sb.AppendLine("- For a partially_fulfilled action, split it: \"fulfilled_part\" is the part now satisfied and \"remaining_part\" is what is still to be done, each written as a standalone action sentence.");
        sb.AppendLine("- Include every gap and every action exactly once, using their [G#] / [A#] labels.");
        sb.AppendLine();
        sb.AppendLine("Respond with ONLY a JSON object (no markdown fences) of this shape:");
        sb.AppendLine("""
{
  "summary": "2-3 sentences on what the new evidence changes for this clause",
  "gaps": [
    { "gap": "G1", "outcome": "fulfilled | partially_fulfilled | not_fulfilled", "covered": "...", "remaining": "...",
      "evidence": [ { "source": "S1", "quote": "verbatim text from that section" } ] }
  ],
  "actions": [
    { "action": "A1", "outcome": "fulfilled | partially_fulfilled | not_fulfilled", "fulfilled_part": "...", "remaining_part": "...", "reason": "..." }
  ]
}
""");
        return sb.ToString();
    }

    // ---------------------------------------------------------------- outcome

    private sealed record OutcomeDelta(int Fulfilled, int Partial, int Open, int Resolved, int Split, string? NewFinalStatus);

    private async Task ApplyOutcomeAsync(
        NdGapEvidenceRerun rerun,
        ClauseWork work,
        EvidenceJudgment judgment,
        CancellationToken ct)
    {
        var docLabel = string.Join("; ", work.Docs.Select(d => d.Name));
        var stamp = DateTimeOffset.UtcNow.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture) + " UTC";
        var (gapResults, actionResults) = EvaluateVerdicts(work, judgment, docLabel);
        foreach (var quote in gapResults.SelectMany(g => g.Quotes))
        {
            if (quote.DocumentId is Guid docId && wordLayouts.TryGetValue(docId, out var layout))
                quote.Page = DocxRenderedPages.PageOf(layout, quote.Text) ?? quote.Page;
        }

        // Each clause is written on its own context inside one transaction, so a failure leaves that
        // clause exactly as it was and never touches the rerun's other clauses.
        using var scope = scopeFactory.CreateScope();
        var applyDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var strategy = applyDb.Database.CreateExecutionStrategy();
        var delta = await strategy.ExecuteAsync(async () =>
        {
            applyDb.ChangeTracker.Clear();
            foreach (var g in gapResults) g.AddedActionPlanId = null;
            foreach (var a in actionResults)
            {
                a.Applied = "unchanged";
                a.NewActionPlanId = null;
            }

            await using var tx = await applyDb.Database.BeginTransactionAsync(ct);
            var result = await PersistOutcomeAsync(applyDb, work, gapResults, actionResults, docLabel, stamp, rerun.CreatedBy, ct);
            await tx.CommitAsync(ct);
            return result;
        });

        var clauseOutcome = gapResults.All(g => g.Outcome == GapEvidenceOutcomes.Fulfilled)
            ? GapEvidenceOutcomes.Fulfilled
            : gapResults.Any(g => g.Outcome != GapEvidenceOutcomes.NotFulfilled)
                ? GapEvidenceOutcomes.Partial
                : GapEvidenceOutcomes.NotFulfilled;

        var review = work.Review;
        review.Status = GapEvidenceStatuses.Completed;
        review.ClauseOutcome = clauseOutcome;
        review.NewFinalStatus = delta.NewFinalStatus;
        review.Summary = Clean(judgment.Summary);
        review.GapsJson = Serialize(gapResults);
        review.ActionsJson = Serialize(actionResults);
        review.ReanalysisJson = work.Reanalysis == null ? null : JsonSerializer.Serialize(work.Reanalysis);
        review.Error = null;
        review.CompletedAt = DateTimeOffset.UtcNow;
        review.UpdatedAt = DateTimeOffset.UtcNow;
        rerun.CompletedPoints++;
        rerun.FulfilledGaps += delta.Fulfilled;
        rerun.PartialGaps += delta.Partial;
        rerun.OpenGaps += delta.Open;
        rerun.ResolvedActions += delta.Resolved;
        rerun.SplitActions += delta.Split;
        rerun.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Turns the model's reply into per-gap and per-action verdicts. Quotes are checked
    /// against the sections the model saw; a verdict with no quoted support never closes a gap, and an
    /// action can only count as done when the evidence touches the gap it belongs to.</summary>
    internal static (List<StoredGap> Gaps, List<StoredAction> Actions) EvaluateVerdicts(
        ClauseWork work,
        EvidenceJudgment judgment,
        string docLabel)
    {
        var sectionByLabel = work.Sections.ToDictionary(s => s.Label, StringComparer.OrdinalIgnoreCase);

        var gapResults = new List<StoredGap>();
        foreach (var gap in work.Gaps)
        {
            var verdict = judgment.Gaps.FirstOrDefault(v => LabelNumber(v.Gap, 'G') == gap.Index);
            var result = new StoredGap
            {
                Index = gap.Index,
                Text = gap.Text,
                Outcome = GapEvidenceOutcomes.Normalize(verdict?.Outcome),
                Covered = Clean(verdict?.Covered),
                Remaining = Clean(verdict?.Remaining),
                Quotes = (verdict?.Evidence ?? [])
                    .Where(q => !string.IsNullOrWhiteSpace(q.Quote))
                    .Select(q => ToQuote(q, sectionByLabel, work.Sections))
                    .ToList(),
            };

            if (result.Outcome == GapEvidenceOutcomes.NotFulfilled)
            {
                result.Covered = null;
                result.Remaining ??= gap.Text;
            }
            else if (result.Quotes.Count == 0)
            {
                result.Outcome = GapEvidenceOutcomes.Partial;
                result.Remaining = "The model gave no quote from the new document for this — confirm manually."
                    + (string.IsNullOrWhiteSpace(result.Remaining) ? "" : " " + result.Remaining);
            }
            else if (result.Outcome == GapEvidenceOutcomes.Fulfilled)
            {
                result.Remaining = null;
            }
            else if (string.IsNullOrWhiteSpace(result.Remaining))
            {
                result.Remaining = $"Parts of this gap not covered by {docLabel}.";
            }
            gapResults.Add(result);
        }

        var actionResults = new List<StoredAction>();
        foreach (var action in work.Actions)
        {
            var verdict = judgment.Actions.FirstOrDefault(v => LabelNumber(v.Action, 'A') == LabelNumber(action.Label, 'A'));
            var gapResult = gapResults.FirstOrDefault(g => g.Index == action.GapIndex);
            var outcome = GapEvidenceOutcomes.Normalize(verdict?.Outcome);
            var fulfilledPart = Clean(verdict?.FulfilledPart);
            var remainingPart = Clean(verdict?.RemainingPart);
            var reason = Clean(verdict?.Reason);

            if (outcome != GapEvidenceOutcomes.NotFulfilled && gapResult?.Outcome == GapEvidenceOutcomes.NotFulfilled)
            {
                outcome = GapEvidenceOutcomes.NotFulfilled;
                reason = "Not resolved: the evidence does not address the gap this action belongs to.";
            }
            if (outcome == GapEvidenceOutcomes.Partial && (fulfilledPart == null || remainingPart == null))
            {
                outcome = fulfilledPart != null && remainingPart == null && gapResult?.Outcome == GapEvidenceOutcomes.Fulfilled
                    ? GapEvidenceOutcomes.Fulfilled
                    : GapEvidenceOutcomes.NotFulfilled;
            }

            actionResults.Add(new StoredAction
            {
                ActionPlanId = action.PlanId,
                GapIndex = action.GapIndex,
                OriginalText = action.Text,
                Outcome = outcome,
                FulfilledPart = outcome == GapEvidenceOutcomes.NotFulfilled ? null : fulfilledPart,
                RemainingPart = outcome == GapEvidenceOutcomes.Partial ? remainingPart : null,
                Reason = reason,
            });
        }

        return (gapResults, actionResults);
    }

    private static async Task<OutcomeDelta> PersistOutcomeAsync(
        AppDbContext ctx,
        ClauseWork work,
        List<StoredGap> gapResults,
        List<StoredAction> actionResults,
        string docLabel,
        string stamp,
        Guid? changedBy,
        CancellationToken ct)
    {
        var pointId = work.Point.Id;
        int resolved = 0, split = 0, fulfilled = 0, partial = 0, open = 0;

        // Resolve what the evidence shows is done; split what is only partly done.
        var planIds = actionResults.Select(a => a.ActionPlanId).ToList();
        var plans = await ctx.NdAnalysisActionPlans.Where(p => planIds.Contains(p.Id)).ToListAsync(ct);
        foreach (var result in actionResults)
        {
            var plan = plans.FirstOrDefault(p => p.Id == result.ActionPlanId);
            if (plan == null || plan.Status == ActionPlanStatuses.Resolved) continue;

            if (result.Outcome == GapEvidenceOutcomes.Fulfilled)
            {
                ResolvePlan(ctx, plan, changedBy,
                    $"Evidence review {stamp}: fulfilled by {docLabel}." + (result.Reason == null ? "" : $" {result.Reason}"));
                result.Applied = "resolved";
                resolved++;
            }
            else if (result.Outcome == GapEvidenceOutcomes.Partial)
            {
                var followUp = await SplitPlanAsync(ctx, plan, result.FulfilledPart!, result.RemainingPart!, docLabel, stamp, changedBy, ct);
                result.Applied = "split";
                result.NewActionPlanId = followUp.Id;
                split++;
            }
        }
        await ctx.SaveChangesAsync(ct);

        // Keep each gap's verdict consistent with what is left open on it, and make sure its row exists.
        var gapIndexes = gapResults.Select(g => g.Index).ToList();
        var gapRows = await ctx.NdAnalysisGaps
            .Where(g => g.AnalysisPointId == pointId && gapIndexes.Contains(g.GapIndex))
            .ToListAsync(ct);
        var pointPlans = await ctx.NdAnalysisActionPlans.Where(p => p.AnalysisPointId == pointId).ToListAsync(ct);
        var point = await ctx.NdAnalysisPoints.FirstAsync(p => p.Id == pointId, ct);

        foreach (var gap in gapResults)
        {
            var mine = pointPlans.Where(p => EffectiveGapIndex(p.GapIndex) == gap.Index).ToList();
            var pending = mine.Where(p => p.Status != ActionPlanStatuses.Resolved).ToList();

            if (gap.Outcome == GapEvidenceOutcomes.Fulfilled && pending.Count > 0)
            {
                gap.Outcome = GapEvidenceOutcomes.Partial;
                gap.Remaining = "Still open actions: " + string.Join(" ", pending.Select(p => p.ActionPlan.Trim()));
            }
            else if (gap.Outcome != GapEvidenceOutcomes.Fulfilled && mine.Count > 0 && pending.Count == 0)
            {
                // Every action on the gap is done but part of the gap is still missing — raise an
                // action for that remainder so the gap stays open and owned.
                var template = mine.OrderByDescending(p => p.UpdatedAt).First();
                var remainder = await AddRemainderPlanAsync(ctx, template, gap.Remaining ?? gap.Text ?? "", docLabel, stamp, changedBy, ct);
                gap.AddedActionPlanId = remainder.Id;
            }

            var row = gapRows.FirstOrDefault(g => g.GapIndex == gap.Index);
            if (row == null)
            {
                row = new NdAnalysisGap
                {
                    AnalysisRunId = point.AnalysisRunId,
                    AnalysisPointId = point.Id,
                    TenantId = point.TenantId,
                    GapIndex = gap.Index,
                    Status = GapStatuses.Pending,
                };
                ctx.NdAnalysisGaps.Add(row);
            }

            // A gap with no actions has nothing to roll up from, so the verdict resolves it directly.
            if (gap.Outcome == GapEvidenceOutcomes.Fulfilled && mine.Count == 0 && row.Status != GapStatuses.Resolved)
            {
                row.Status = GapStatuses.Resolved;
                row.ResolvedAt = DateTimeOffset.UtcNow;
                row.ResolvedBy = changedBy;
                row.UpdatedBy = changedBy;
                row.UpdatedAt = DateTimeOffset.UtcNow;
            }

            if (gap.Outcome == GapEvidenceOutcomes.Fulfilled) fulfilled++;
            else if (gap.Outcome == GapEvidenceOutcomes.Partial) partial++;
            else open++;
        }
        await ctx.SaveChangesAsync(ct);

        // Gap → clause roll-up: the clause reads compliant once every gap is resolved.
        await NdGapStatusResolver.RecomputeAsync(ctx, [pointId], ct);

        // Partial coverage of a non-compliant clause makes it partially compliant; a verdict a
        // person set by hand is never overridden.
        var anyCoverage = gapResults.Any(g => g.Outcome != GapEvidenceOutcomes.NotFulfilled);
        if (point.FinalStatusSource != "manual" && point.FinalStatus == ClauseStatuses.NonCompliant && anyCoverage)
        {
            point.AiFinalStatus ??= ClauseStatuses.NonCompliant;
            point.FinalStatus = ClauseStatuses.Partial;
            point.UpdatedAt = DateTimeOffset.UtcNow;
            await ctx.SaveChangesAsync(ct);
        }

        return new OutcomeDelta(fulfilled, partial, open, resolved, split, point.FinalStatus);
    }

    private static void ResolvePlan(AppDbContext ctx, NdAnalysisActionPlan plan, Guid? changedBy, string note)
    {
        var now = DateTimeOffset.UtcNow;
        ctx.NdAnalysisActionPlanStatusHistories.Add(new NdAnalysisActionPlanStatusHistory
        {
            ActionPlanId = plan.Id,
            TenantId = plan.TenantId,
            PreviousStatus = plan.Status,
            NewStatus = ActionPlanStatuses.Resolved,
            ChangedBy = changedBy,
        });
        plan.Status = ActionPlanStatuses.Resolved;
        plan.ResolvedAt = now;
        plan.ResolvedBy = changedBy;
        plan.UpdatedBy = changedBy;
        plan.UpdatedAt = now;
        plan.Comment = AppendNote(plan.Comment, note);
    }

    /// <summary>The original action keeps the part the evidence satisfies and is resolved; the rest moves
    /// to a new pending action on the same gap with the same owners, date and priority. The original
    /// wording stays in the resolved action's comment and in the evidence review.</summary>
    private static async Task<NdAnalysisActionPlan> SplitPlanAsync(
        AppDbContext ctx,
        NdAnalysisActionPlan plan,
        string fulfilledPart,
        string remainingPart,
        string docLabel,
        string stamp,
        Guid? changedBy,
        CancellationToken ct)
    {
        var original = plan.ActionPlan.Trim();
        plan.ActionPlan = fulfilledPart.Trim();
        ResolvePlan(ctx, plan, changedBy,
            $"Evidence review {stamp}: partly fulfilled by {docLabel}. This part is done; the rest moved to a new action. Original action: \"{original}\"");

        var followUp = CopyPlan(plan, remainingPart.Trim(), changedBy,
            $"Evidence review {stamp}: remaining part of an action that {docLabel} only partly fulfilled. Original action: \"{original}\"");
        await AddPlanWithAssigneesAsync(ctx, followUp, plan.Id, ct);
        return followUp;
    }

    private static async Task<NdAnalysisActionPlan> AddRemainderPlanAsync(
        AppDbContext ctx,
        NdAnalysisActionPlan template,
        string remaining,
        string docLabel,
        string stamp,
        Guid? changedBy,
        CancellationToken ct)
    {
        var plan = CopyPlan(template, $"Address the part of this gap still missing: {remaining.Trim()}", changedBy,
            $"Evidence review {stamp}: added because {docLabel} covers this gap only in part.");
        await AddPlanWithAssigneesAsync(ctx, plan, template.Id, ct);
        return plan;
    }

    private static NdAnalysisActionPlan CopyPlan(NdAnalysisActionPlan source, string text, Guid? changedBy, string comment) => new()
    {
        AnalysisRunId = source.AnalysisRunId,
        AnalysisPointId = source.AnalysisPointId,
        TenantId = source.TenantId,
        GapIndex = source.GapIndex,
        ActionPlan = text,
        Status = ActionPlanStatuses.Pending,
        PriorityScore = source.PriorityScore,
        Priority = source.Priority,
        TargetDate = source.TargetDate,
        ResponsibilityType = source.ResponsibilityType,
        ResponsibilityDepartmentId = source.ResponsibilityDepartmentId,
        ResponsibilityUserId = source.ResponsibilityUserId,
        ResponsibilityLabel = source.ResponsibilityLabel,
        Comment = comment,
        SortOrder = source.SortOrder + 1,
        CreatedBy = changedBy,
        UpdatedBy = changedBy,
    };

    private static async Task AddPlanWithAssigneesAsync(
        AppDbContext ctx,
        NdAnalysisActionPlan plan,
        Guid copyAssigneesFrom,
        CancellationToken ct)
    {
        // Saved on its own first: the history and assignee rows reference it by foreign key, and
        // EF has no navigation between them to order the inserts.
        ctx.NdAnalysisActionPlans.Add(plan);
        await ctx.SaveChangesAsync(ct);
        ctx.NdAnalysisActionPlanStatusHistories.Add(new NdAnalysisActionPlanStatusHistory
        {
            ActionPlanId = plan.Id,
            TenantId = plan.TenantId,
            PreviousStatus = null,
            NewStatus = ActionPlanStatuses.Pending,
            ChangedBy = plan.CreatedBy,
        });

        var assignees = await ctx.NdAnalysisActionPlanAssignees.AsNoTracking()
            .Where(a => a.ActionPlanId == copyAssigneesFrom)
            .OrderBy(a => a.SortOrder)
            .ToListAsync(ct);
        foreach (var a in assignees)
        {
            ctx.NdAnalysisActionPlanAssignees.Add(new NdAnalysisActionPlanAssignee
            {
                ActionPlanId = plan.Id,
                TenantId = plan.TenantId,
                AssigneeType = a.AssigneeType,
                DepartmentId = a.DepartmentId,
                UserId = a.UserId,
                Label = a.Label,
                SortOrder = a.SortOrder,
            });
        }
    }

    // ---------------------------------------------------------------- helpers

    private static int EffectiveGapIndex(int gapIndex) => gapIndex <= 0 ? 1 : gapIndex;

    private static List<StoredGap> SanitizeGaps(List<GapInput>? gaps, int? gapIndexFilter)
    {
        var list = (gaps ?? [])
            .Where(g => g.Index > 0 && !string.IsNullOrWhiteSpace(g.Text))
            .GroupBy(g => g.Index)
            .Select(g => new StoredGap { Index = g.Key, Text = Truncate(g.First().Text!.Trim(), MaxGapTextChars) })
            .OrderBy(g => g.Index)
            .ToList();
        if (gapIndexFilter.HasValue)
            list = list.Where(g => g.Index == gapIndexFilter.Value).ToList();
        return list;
    }

    /// <summary>Used when the page did not send its gap list: one gap per known gap index, worded from
    /// the clause's own gap description and the actions raised against it.</summary>
    private static List<StoredGap> FallbackGaps(
        RegulJudgmentResult? prior,
        IEnumerable<NdAnalysisGap> gapRows,
        List<NdAnalysisActionPlan> plans,
        int? gapIndexFilter)
    {
        var indexes = gapRows.Select(g => g.GapIndex)
            .Concat(plans.Select(p => EffectiveGapIndex(p.GapIndex)))
            .Where(i => i > 0)
            .Distinct()
            .OrderBy(i => i)
            .ToList();
        if (gapIndexFilter.HasValue) indexes = [gapIndexFilter.Value];
        if (indexes.Count == 0) indexes = [1];

        var description = prior?.GapDescription?.Trim();
        if (string.IsNullOrWhiteSpace(description) || description == "N/A") description = null;

        return indexes.Select(i =>
        {
            var actions = plans.Where(p => EffectiveGapIndex(p.GapIndex) == i).Select(p => p.ActionPlan.Trim()).ToList();
            var text = indexes.Count == 1 && description != null
                ? description
                : actions.Count > 0
                    ? $"Gap {i}: missing what these actions would put in place — {string.Join(" ", actions)}"
                    : description ?? $"Gap {i}";
            return new StoredGap { Index = i, Text = Truncate(text, MaxGapTextChars) };
        }).ToList();
    }

    private async Task LoadWordLayoutsAsync(IReadOnlyList<Guid> docIds, CancellationToken ct)
    {
        if (!storage.IsConfigured || docIds.Count == 0) return;
        var docs = await db.StoredDocuments.AsNoTracking()
            .Where(d => docIds.Contains(d.Id))
            .Select(d => new { d.Id, d.OriginalFileName, d.StoragePath })
            .ToListAsync(ct);
        foreach (var doc in docs.Where(d => DocxRenderedPages.IsWordFile(d.OriginalFileName) && !string.IsNullOrWhiteSpace(d.StoragePath)))
        {
            try
            {
                var layout = DocxRenderedPages.Read(await storage.DownloadAsync(doc.StoragePath!, ct));
                if (layout is { PageCount: > 1 }) wordLayouts[doc.Id] = layout;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not read Word layout for evidence document {DocId}", doc.Id);
            }
        }
    }

    private async Task<List<ContextSection>> LoadEvidenceSectionsAsync(List<EvidenceDocRef> docs, int limit, CancellationToken ct)
    {
        var docIds = docs.Select(d => d.Id).ToList();
        var extractions = await db.NdLocalDocumentExtractions.AsNoTracking()
            .Where(e => docIds.Contains(e.StoredDocumentId) && e.Engine == NdGapEvidencePrepareService.DefaultLocalEngine)
            .Select(e => new { e.Id, e.StoredDocumentId })
            .ToListAsync(ct);
        var extractionIds = extractions.Select(e => e.Id).ToList();
        var sections = await db.NdLocalDocumentExtractionSections.AsNoTracking()
            .Where(s => extractionIds.Contains(s.ExtractionId))
            .OrderBy(s => s.ExtractionId).ThenBy(s => s.SectionIndex)
            .Take(limit)
            .Select(s => new { s.ExtractionId, s.ClauseNo, s.ClauseText, s.SourcePage })
            .ToListAsync(ct);

        return sections.Select(s =>
        {
            var docId = extractions.First(e => e.Id == s.ExtractionId).StoredDocumentId;
            return new ContextSection
            {
                DocumentId = docId,
                DocumentName = docs.FirstOrDefault(d => d.Id == docId)?.Name ?? "document",
                SectionRef = s.ClauseNo,
                Page = s.SourcePage,
                Text = s.ClauseText,
            };
        }).ToList();
    }

    private static List<ContextSection> TrimContext(List<ContextSection> sections)
    {
        var kept = new List<ContextSection>();
        var total = 0;
        foreach (var s in sections)
        {
            s.Text = Truncate((s.Text ?? "").Trim(), MaxSectionChars);
            if (s.Text.Length == 0) continue;
            if (total + s.Text.Length > MaxContextChars && kept.Count > 0) break;
            total += s.Text.Length;
            kept.Add(s);
        }
        return kept;
    }

    private static StoredQuote ToQuote(
        EvidenceQuoteVerdict q,
        Dictionary<string, ContextSection> sectionByLabel,
        List<ContextSection> sections)
    {
        var quote = q.Quote!.Trim().Trim('"', '“', '”');
        var normalizedQuote = NormalizeForMatch(quote);
        sectionByLabel.TryGetValue((q.Source ?? "").Trim().Trim('[', ']'), out var section);
        var verified = section != null && NormalizeForMatch(section.Text ?? "").Contains(normalizedQuote);
        if (!verified)
        {
            var match = sections.FirstOrDefault(s => NormalizeForMatch(s.Text ?? "").Contains(normalizedQuote));
            if (match != null)
            {
                section = match;
                verified = true;
            }
        }

        return new StoredQuote
        {
            Text = quote,
            Verified = verified,
            DocumentId = section?.DocumentId,
            DocumentName = section?.DocumentName,
            SectionRef = section?.SectionRef,
            Page = section?.Page,
        };
    }

    private static string NormalizeForMatch(string text) =>
        Regex.Replace(text.ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();

    private static int? LabelNumber(string? label, char prefix)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        var m = Regex.Match(label, @"\d+");
        if (!m.Success) return null;
        var t = label.Trim().TrimStart('[');
        if (char.IsLetter(t[0]) && char.ToUpperInvariant(t[0]) != prefix && !t.StartsWith("gap", StringComparison.OrdinalIgnoreCase)
            && !t.StartsWith("action", StringComparison.OrdinalIgnoreCase))
            return null;
        return int.Parse(m.Value, CultureInfo.InvariantCulture);
    }

    private static string? Clean(string? value)
    {
        var t = value?.Trim();
        if (string.IsNullOrWhiteSpace(t)) return null;
        return t is "N/A" or "n/a" or "-" or "—" or "None" or "none" ? null : t;
    }

    private static string AppendNote(string? existing, string note) =>
        string.IsNullOrWhiteSpace(existing) ? note : $"{existing.TrimEnd()}\n{note}";

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    private static string StatusLabel(string? status) => (status ?? "").Trim().ToLowerInvariant() switch
    {
        "compliant" => "Compliant",
        "partial_compliant" or "partial" or "partially_compliant" => "Partially compliant",
        "non_compliant" or "noncompliant" => "Non-compliant",
        _ => "Not compliant",
    };

    private static RegulJudgmentResult? TryParseJudgment(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<RegulJudgmentResult>(json);
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveClauseNo(NdAnalysisPoint point, NdRegulForwardFinding? finding) =>
        ResolveClause(point, finding).ClauseNo;

    private static (string ClauseNo, string ClauseText) ResolveClause(NdAnalysisPoint point, NdRegulForwardFinding? finding)
    {
        string no = finding?.ClauseNo ?? "", text = finding?.ClauseText ?? "";
        if (string.IsNullOrWhiteSpace(no) || string.IsNullOrWhiteSpace(text))
        {
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(point.PointSnapshot) ? "{}" : point.PointSnapshot);
                var root = doc.RootElement;
                string Read(params string[] keys)
                {
                    foreach (var k in keys)
                        if (root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
                            return v.GetString()!;
                    return "";
                }
                if (string.IsNullOrWhiteSpace(no)) no = Read("pointNumber", "point_number");
                if (string.IsNullOrWhiteSpace(text)) text = Read("pointContent", "pointText", "point_text", "text", "pointTitle");
            }
            catch (JsonException)
            {
                /* keep what the finding had */
            }
        }
        return (no.Trim().TrimStart('§').Trim(), text.Trim());
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, StoreJson);

    private static T? Deserialize<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;
        try
        {
            return JsonSerializer.Deserialize<T>(json, StoreJson);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    // ------------------------------------------------------------ read models

    public async Task<NdGapEvidenceRerun?> LatestAsync(Guid runId, CancellationToken ct)
    {
        var latest = await db.NdGapEvidenceReruns
            .Where(r => r.AnalysisRunId == runId)
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (latest != null) await MarkInterruptedIfStaleAsync(latest, ct);
        return latest;
    }

    /// <summary>Progress payload the page polls: run-level phase and counts plus one row per clause.</summary>
    public async Task<object> BuildRerunDtoAsync(NdGapEvidenceRerun rerun, CancellationToken ct)
    {
        var rows = await db.NdGapEvidenceReviews.AsNoTracking()
            .Where(r => r.RerunId == rerun.Id)
            .OrderBy(r => r.CreatedAt)
            .Select(r => new { r.Id, r.AnalysisPointId, r.GapIndexFilter, r.ClauseNo, r.Status, r.ClauseOutcome, r.Error, r.RetrievalJson })
            .ToListAsync(ct);
        var items = rows.Select(r => new
        {
            reviewId = r.Id,
            analysisPointId = r.AnalysisPointId,
            gapIndexFilter = r.GapIndexFilter,
            clauseNo = r.ClauseNo,
            status = r.Status,
            clauseOutcome = r.ClauseOutcome,
            error = r.Error,
            retrieval = string.IsNullOrWhiteSpace(r.RetrievalJson) ? null : System.Text.Json.Nodes.JsonNode.Parse(r.RetrievalJson),
        }).ToList();

        var createdByName = rerun.CreatedBy.HasValue
            ? await db.NdProfiles.AsNoTracking().Where(p => p.Id == rerun.CreatedBy).Select(p => p.FullName).FirstOrDefaultAsync(ct)
            : null;

        return new
        {
            id = rerun.Id,
            analysisRunId = rerun.AnalysisRunId,
            scope = rerun.Scope,
            status = rerun.Status,
            phase = rerun.Phase,
            phaseDetail = rerun.PhaseDetail,
            evidenceDocuments = Deserialize<List<EvidenceDocRef>>(rerun.EvidenceDocumentsJson) ?? [],
            totalPoints = rerun.TotalPoints,
            completedPoints = rerun.CompletedPoints,
            failedPoints = rerun.FailedPoints,
            fulfilledGaps = rerun.FulfilledGaps,
            partialGaps = rerun.PartialGaps,
            openGaps = rerun.OpenGaps,
            resolvedActions = rerun.ResolvedActions,
            splitActions = rerun.SplitActions,
            error = rerun.Error,
            llmProvider = rerun.LlmProvider,
            llmModel = rerun.LlmModel,
            createdByName,
            startedAt = rerun.StartedAt,
            finishedAt = rerun.FinishedAt,
            createdAt = rerun.CreatedAt,
            updatedAt = rerun.UpdatedAt,
            items,
        };
    }

    /// <summary>Every finished evidence review on a run, newest first — the per-clause history shown
    /// beside the clause's original gaps.</summary>
    public static async Task<List<object>> LoadReviewsForRunAsync(AppDbContext ctx, Guid runId, CancellationToken ct)
    {
        var reviews = await ctx.NdGapEvidenceReviews.AsNoTracking()
            .Where(r => r.AnalysisRunId == runId
                && (r.Status == GapEvidenceStatuses.Completed || r.Status == GapEvidenceStatuses.Failed))
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(ct);
        if (reviews.Count == 0) return [];

        var rerunIds = reviews.Select(r => r.RerunId).Distinct().ToList();
        var reruns = await ctx.NdGapEvidenceReruns.AsNoTracking()
            .Where(r => rerunIds.Contains(r.Id))
            .Select(r => new { r.Id, r.CreatedBy, r.LlmProvider, r.LlmModel, r.Scope })
            .ToListAsync(ct);
        var creatorIds = reruns.Where(r => r.CreatedBy.HasValue).Select(r => r.CreatedBy!.Value).Distinct().ToList();
        var names = creatorIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await ctx.NdProfiles.AsNoTracking()
                .Where(p => creatorIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, p => p.FullName, ct);

        return reviews.Select(r =>
        {
            var rerun = reruns.FirstOrDefault(x => x.Id == r.RerunId);
            return (object)new
            {
                id = r.Id,
                rerunId = r.RerunId,
                analysisPointId = r.AnalysisPointId,
                gapIndexFilter = r.GapIndexFilter,
                scope = rerun?.Scope,
                status = r.Status,
                clauseNo = r.ClauseNo,
                clauseOutcome = r.ClauseOutcome,
                priorFinalStatus = r.PriorFinalStatus,
                newFinalStatus = r.NewFinalStatus,
                summary = r.Summary,
                evidenceDocuments = Deserialize<List<EvidenceDocRef>>(r.EvidenceDocumentsJson) ?? [],
                gaps = Deserialize<List<StoredGap>>(r.GapsJson) ?? [],
                actions = Deserialize<List<StoredAction>>(r.ActionsJson) ?? [],
                reanalysis = TryParseJudgment(r.ReanalysisJson) is { } re
                    ? new
                    {
                        overallStatus = re.OverallStatus,
                        confidence = re.Confidence,
                        interpretation = re.Interpretation,
                        gapDescription = re.GapDescription,
                        suggestedAction = re.SuggestedAction,
                        documentReference = re.DocumentReference,
                        policyExtract = re.PolicyExtract,
                    }
                    : null,
                error = r.Error,
                llmProvider = rerun?.LlmProvider,
                llmModel = rerun?.LlmModel,
                createdByName = rerun?.CreatedBy is Guid by && names.TryGetValue(by, out var n) ? n : null,
                createdAt = r.CreatedAt,
                completedAt = r.CompletedAt,
            };
        }).ToList();
    }

    public sealed record EvidenceDocRef(Guid Id, string Name);

    internal sealed record ActionRef(string Label, Guid PlanId, int GapIndex, string Text);

    public sealed class StoredGap
    {
        public int Index { get; set; }
        public string? Text { get; set; }
        public string? Outcome { get; set; }
        public string? Covered { get; set; }
        public string? Remaining { get; set; }
        public List<StoredQuote> Quotes { get; set; } = [];
        public Guid? AddedActionPlanId { get; set; }
    }

    public sealed class StoredQuote
    {
        public string Text { get; set; } = "";
        public bool Verified { get; set; }
        public Guid? DocumentId { get; set; }
        public string? DocumentName { get; set; }
        public string? SectionRef { get; set; }
        public int? Page { get; set; }
    }

    public sealed class StoredAction
    {
        public Guid ActionPlanId { get; set; }
        public int GapIndex { get; set; }
        public string OriginalText { get; set; } = "";
        public string Outcome { get; set; } = GapEvidenceOutcomes.NotFulfilled;
        public string? FulfilledPart { get; set; }
        public string? RemainingPart { get; set; }
        public string? Reason { get; set; }
        /// <summary>resolved | split | unchanged</summary>
        public string Applied { get; set; } = "unchanged";
        public Guid? NewActionPlanId { get; set; }
    }

    internal sealed class ContextSection
    {
        public string Label { get; set; } = "";
        public Guid DocumentId { get; set; }
        public string DocumentName { get; set; } = "";
        public string? SectionRef { get; set; }
        public int? Page { get; set; }
        public double? Score { get; set; }
        public string? Text { get; set; }

        public object ForStorage() => new
        {
            label = Label,
            documentId = DocumentId,
            documentName = DocumentName,
            sectionRef = SectionRef,
            page = Page,
            score = Score,
            text = Text == null ? null : Truncate(Text, 600),
        };
    }

    // The model's reply. Labels may come back as "G1", 1 or "Gap 1", so they are read leniently.
    internal sealed class EvidenceJudgment
    {
        [JsonConverter(typeof(LenientStringConverter))]
        public string? Summary { get; set; }
        public List<EvidenceGapVerdict> Gaps { get; set; } = [];
        public List<EvidenceActionVerdict> Actions { get; set; } = [];
    }

    internal sealed class EvidenceGapVerdict
    {
        [JsonConverter(typeof(LenientStringConverter))]
        public string? Gap { get; set; }
        [JsonConverter(typeof(LenientStringConverter))]
        public string? Outcome { get; set; }
        [JsonConverter(typeof(LenientStringConverter))]
        public string? Covered { get; set; }
        [JsonConverter(typeof(LenientStringConverter))]
        public string? Remaining { get; set; }
        [JsonConverter(typeof(LenientQuoteListConverter))]
        public List<EvidenceQuoteVerdict> Evidence { get; set; } = [];
    }

    internal sealed class EvidenceQuoteVerdict
    {
        [JsonConverter(typeof(LenientStringConverter))]
        public string? Source { get; set; }
        [JsonConverter(typeof(LenientStringConverter))]
        public string? Quote { get; set; }
    }

    internal sealed class EvidenceActionVerdict
    {
        [JsonConverter(typeof(LenientStringConverter))]
        public string? Action { get; set; }
        [JsonConverter(typeof(LenientStringConverter))]
        public string? Outcome { get; set; }
        [JsonConverter(typeof(LenientStringConverter))]
        public string? FulfilledPart { get; set; }
        [JsonConverter(typeof(LenientStringConverter))]
        public string? RemainingPart { get; set; }
        [JsonConverter(typeof(LenientStringConverter))]
        public string? Reason { get; set; }
    }

    /// <summary>The model sometimes answers a text field with a list of points or a number; all of it
    /// is read as text instead of failing the clause.</summary>
    internal sealed class LenientStringConverter : JsonConverter<string?>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) return null;
            using var doc = JsonDocument.ParseValue(ref reader);
            var text = Flatten(doc.RootElement).Trim();
            return text.Length == 0 ? null : text;
        }

        internal static string Flatten(JsonElement el) => el.ValueKind switch
        {
            JsonValueKind.String => el.GetString() ?? "",
            JsonValueKind.Number => el.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Array => string.Join('\n', el.EnumerateArray().Select(Flatten).Where(t => !string.IsNullOrWhiteSpace(t))),
            JsonValueKind.Object => string.Join("; ", el.EnumerateObject().Select(p => Flatten(p.Value)).Where(t => !string.IsNullOrWhiteSpace(t))),
            _ => "",
        };

        public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value);
    }

    /// <summary>Quotes may come back as objects, plain strings, or a single object instead of a list.</summary>
    internal sealed class LenientQuoteListConverter : JsonConverter<List<EvidenceQuoteVerdict>>
    {
        public override List<EvidenceQuoteVerdict> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;
            var items = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToList() : [root];
            var list = new List<EvidenceQuoteVerdict>();
            foreach (var item in items)
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    string? Prop(params string[] names) => names
                        .Select(n => item.EnumerateObject().FirstOrDefault(p => string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase)))
                        .Where(p => p.Value.ValueKind != JsonValueKind.Undefined)
                        .Select(p => LenientStringConverter.Flatten(p.Value))
                        .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
                    list.Add(new EvidenceQuoteVerdict { Source = Prop("source", "section"), Quote = Prop("quote", "text") });
                }
                else
                {
                    var text = LenientStringConverter.Flatten(item);
                    if (!string.IsNullOrWhiteSpace(text)) list.Add(new EvidenceQuoteVerdict { Quote = text });
                }
            }
            return list;
        }

        public override void Write(Utf8JsonWriter writer, List<EvidenceQuoteVerdict> value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value);
    }
}
