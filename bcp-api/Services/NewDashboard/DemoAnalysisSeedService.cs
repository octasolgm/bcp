using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Reguliq.Api.Data;
using Reguliq.Api.Data.Entities;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Models;
using Reguliq.Api.Services;
using Reguliq.Api.Services.LandingAi;
using Reguliq.Api.Services.NewDashboard.Demo;

namespace Reguliq.Api.Services.NewDashboard;

/// <summary>
/// Builds completed ND analysis runs from seeded compliance session data (no AI calls).
/// </summary>
public class DemoAnalysisSeedService(
    AppDbContext db,
    IHostEnvironment env,
    NdDemoWorkspaceService demoWorkspace,
    IMemoryCache cache)
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> DemoRunSyncLocks = new();
    private static readonly TimeSpan DemoRunSyncThrottle = TimeSpan.FromSeconds(45);

    private static readonly JsonSerializerOptions JudgmentJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public const string CbuaeAmlSeedFileName = "cbuae-aml-demo-judgments.json";
    public const string CbuaeRegulationExtractSeedFileName = "regulation-points-extract.json";
    public const string TfsRegulationExtractSeedFileName = "gov-tfs-guidelines.extract.json";
    public const string ImptfsInternalFileHash =
        "6a0a0bd13c7a32ea10c43c9a8391347a7e0caceaa0b17dd6443e9ee622111717";

    /// <summary>Dev/scripts only — runtime demo clones Landing AI points from the production template in DB.</summary>
    public sealed record RegulationExtractPointSeed(
        string PointNumber,
        string? PointTitle,
        string PointContent,
        string? PageReference,
        bool IsIntroductionPoint,
        bool IsAnnexPoint);
    public static readonly Guid SeededComplianceSessionId =
        Guid.Parse("a339de5e-06b9-4067-bd97-e7d8086bf31e");

    public record DemoPointInput(
        string PointId,
        string? Title,
        string? Text,
        string? LandingMessage,
        string? LlmMessage,
        object? AgreementJson);

    public record DemoSaveRequest(
        string? Name,
        List<object>? SelectedPointsSnapshot,
        List<string>? SelectedInternalDocIds,
        List<string>? SelectedRegulationDocIds,
        List<DemoPointInput>? Points);

    public record DemoRegulSaveRequest(
        string? Name = null,
        string? RegulationDocumentId = null,
        string? InternalDocumentId = null,
        string? RegulationNameHint = null,
        string? InternalNameHint = null,
        List<DemoRegulJudgmentRow>? Judgments = null,
        bool UseSeedFile = false);

    public sealed class DemoRegulJudgmentRow
    {
        [JsonPropertyName("clause_no")]
        public string ClauseNo { get; set; } = "";

        [JsonPropertyName("clause_title")]
        public string? ClauseTitle { get; set; }

        [JsonPropertyName("clause_content")]
        public string? ClauseContent { get; set; }

        [JsonPropertyName("design_status")]
        public string DesignStatus { get; set; } = "";

        [JsonPropertyName("operating_status")]
        public string OperatingStatus { get; set; } = "";

        [JsonPropertyName("overall_status")]
        public string OverallStatus { get; set; } = "";

        [JsonPropertyName("confidence")]
        public double Confidence { get; set; }

        [JsonPropertyName("interpretation")]
        public string Interpretation { get; set; } = "";

        [JsonPropertyName("policy_extract")]
        [JsonConverter(typeof(JsonStringOrArrayConverter))]
        public List<string> PolicyExtract { get; set; } = [];

        [JsonPropertyName("document_reference")]
        public string DocumentReference { get; set; } = "";

        [JsonPropertyName("gap_description")]
        public string GapDescription { get; set; } = "";

        [JsonPropertyName("suggested_action")]
        public string SuggestedAction { get; set; } = "";

        [JsonPropertyName("gap_direction")]
        public string GapDirection { get; set; } = "";

        public RegulJudgmentResult ToJudgmentResult() => new()
        {
            DesignStatus = DesignStatus,
            OperatingStatus = OperatingStatus,
            OverallStatus = OverallStatus,
            Confidence = Confidence,
            Interpretation = Interpretation,
            PolicyExtract = PolicyExtract,
            DocumentReference = DocumentReference,
            GapDescription = NdRegulJudgmentFormatter.ResolveGapDescriptionForSeedRow(
                GapDescription,
                Interpretation,
                OverallStatus,
                DesignStatus),
            SuggestedAction = SuggestedAction,
            GapDirection = GapDirection,
        };
    }

    /// <summary>Creates a completed Regul V4 demo run from Arena / external judgment JSON (no AI).</summary>
    public async Task<NdAnalysisRun> CreateDemoRegulRunFromJudgmentsAsync(
        Guid userId,
        DemoRegulSaveRequest body,
        CancellationToken ct = default)
    {
        var judgments = body.Judgments ?? [];
        if (judgments.Count == 0 && body.UseSeedFile)
            judgments = LoadCbuaeSeedJudgments();
        if (judgments.Count == 0)
            throw new InvalidOperationException("No Regul judgments to save.");

        var regDocId = await ResolveRegulationDocumentIdAsync(userId, body, ct);
        var intDocId = await ResolveInternalDocumentIdAsync(userId, body, ct);

        var regPoints = await db.NdRegulationPoints.AsNoTracking()
            .Where(p => p.RegulationDocumentId == regDocId && p.Status == NdRegulationPointStatus.Active)
            .OrderBy(p => p.PointNumber)
            .ToListAsync(ct);
        if (regPoints.Count == 0)
            throw new InvalidOperationException("Regulation document has no extracted points. Run Extract first.");

        var matched = MatchAllJudgmentsToRegulationPoints(regPoints, judgments);
        if (matched.Count == 0)
            throw new InvalidOperationException("No judgments matched regulation points by clause number.");

        var regDoc = await db.NdRegulationDocuments.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == regDocId, ct);
        var regLabel = regDoc?.Name ?? "CBUAE regulation";
        var intDoc = await db.StoredDocuments.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == intDocId, ct);
        var intLabel = intDoc?.OriginalFileName ?? intDoc?.Title ?? "Internal manual";
        var runName = string.IsNullOrWhiteSpace(body.Name)
            ? $"[Demo] {intLabel} × {regLabel}".Trim()
            : body.Name.Trim();

        var run = new NdAnalysisRun
        {
            Name = runName.Length > 240 ? runName[..240] : runName,
            Description = "Demonstration Regul V4 run — Arena judgments seeded, no AI credits used.",
            WorkflowEngine = AnalysisWorkflowEngine.RegulPipelineFull,
            ComparePromptVersion = ComparePromptVersion.V3.ToApiValue(),
            Status = "completed",
            RegulClausesConfirmedAt = DateTimeOffset.UtcNow,
            SelectedInternalDocIds = JsonSerializer.Serialize(new[] { intDocId.ToString() }),
            SelectedRegulationDocIds = JsonSerializer.Serialize(new[] { regDocId.ToString() }),
            TotalPointsCount = matched.Count,
            ProcessedPointsCount = matched.Count,
            LandingAiCompletedCount = matched.Count,
            DualVerifyCompletedCount = 0,
            DualVerifyFailedCount = 0,
            CreatedBy = userId,
        };
        db.NdAnalysisRuns.Add(run);

        var pendingHistories = new List<(NdAnalysisPoint Point, string Cap, Guid ChangedBy)>();
        foreach (var (regPoint, row) in matched)
        {
            var clauseNo = row.ClauseNo.Trim();
            var clauseText = !string.IsNullOrWhiteSpace(row.ClauseContent)
                ? row.ClauseContent.Trim()
                : regPoint?.PointContent ?? "";
            var judgment = row.ToJudgmentResult();
            var landingMessage = NdRegulJudgmentFormatter.FormatLandingMessage(clauseNo, clauseText, judgment);

            var point = new NdAnalysisPoint
            {
                AnalysisRunId = run.Id,
                RegulationPointId = regPoint?.Id,
                PointSnapshot = JsonSerializer.Serialize(new
                {
                    pointNumber = clauseNo,
                    pointTitle = regPoint?.PointTitle ?? row.ClauseTitle,
                    pointContent = clauseText,
                    pageReference = regPoint?.PageReference,
                    regulationDocumentId = regDocId.ToString(),
                    regulationPointId = regPoint?.Id.ToString(),
                }),
            };
            NdRegulAnalysisPointSync.ApplyForwardJudgment(point, judgment, landingMessage);
            run.Points.Add(point);

            db.NdRegulForwardFindings.Add(new NdRegulForwardFinding
            {
                AnalysisRunId = run.Id,
                AnalysisPointId = point.Id,
                ClauseNo = clauseNo,
                ClauseText = clauseText,
                Status = "completed",
                ResultJson = JsonSerializer.Serialize(judgment, JudgmentJsonOptions),
            });

            var cap = point.LandingAiActionPlan ?? point.FinalActionPlan;
            if (!string.IsNullOrWhiteSpace(cap))
                pendingHistories.Add((point, cap, userId));
        }

        await db.SaveChangesAsync(ct);

        foreach (var (point, cap, changedBy) in pendingHistories)
        {
            db.NdActionPlanHistories.Add(new NdActionPlanHistory
            {
                AnalysisPointId = point.Id,
                VersionNumber = 1,
                ActionPlanContent = cap,
                ChangeType = "ai_original",
                IsCurrent = true,
                ChangedBy = changedBy,
            });
        }

        if (pendingHistories.Count > 0)
            await db.SaveChangesAsync(ct);

        return run;
    }

    public List<DemoRegulJudgmentRow> LoadCbuaeSeedJudgments()
    {
        var path = Path.Combine(env.ContentRootPath, "SeedData", CbuaeAmlSeedFileName);
        if (!File.Exists(path))
            throw new InvalidOperationException($"Seed file not found: SeedData/{CbuaeAmlSeedFileName}");

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<List<DemoRegulJudgmentRow>>(json, JudgmentJsonOptions) ?? [];
    }

    private static List<DemoRegulJudgmentRow>? CbuaeJudgmentsCache;
    private static readonly object CbuaeJudgmentsCacheLock = new();

    public List<DemoRegulJudgmentRow> GetCachedCbuaeSeedJudgments()
    {
        lock (CbuaeJudgmentsCacheLock)
        {
            CbuaeJudgmentsCache = LoadCbuaeSeedJudgments();
            return CbuaeJudgmentsCache;
        }
    }

    public int GetCbuaeExpectedPointCount() => GetCachedCbuaeSeedJudgments().Count;

    private static List<RegulationExtractPointSeed>? RegulationExtractCache;
    private static readonly object RegulationExtractCacheLock = new();

    public List<RegulationExtractPointSeed> LoadRegulationExtractPoints()
    {
        var path = Path.Combine(env.ContentRootPath, "SeedData", CbuaeRegulationExtractSeedFileName);
        if (!File.Exists(path))
            throw new InvalidOperationException($"Seed file not found: SeedData/{CbuaeRegulationExtractSeedFileName}");

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<List<RegulationExtractPointSeed>>(json, JudgmentJsonOptions) ?? [];
    }

    public List<RegulationExtractPointSeed> GetCachedRegulationExtractPoints()
    {
        lock (RegulationExtractCacheLock)
        {
            RegulationExtractCache ??= LoadRegulationExtractPoints();
            return RegulationExtractCache;
        }
    }

    public int GetCbuaeRegulationExtractPointCount() => GetCachedRegulationExtractPoints().Count;

    private static List<RegulationExtractPointSeed>? TfsRegulationExtractCache;

    public List<RegulationExtractPointSeed> LoadTfsRegulationExtractPoints()
    {
        var path = Path.Combine(env.ContentRootPath, "SeedData", TfsRegulationExtractSeedFileName);
        if (!File.Exists(path))
            return [];

        var json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("points", out var pointsEl) || pointsEl.ValueKind != JsonValueKind.Array)
            return [];

        var rows = new List<RegulationExtractPointSeed>();
        foreach (var point in pointsEl.EnumerateArray())
        {
            var pointNumber = point.TryGetProperty("point_id", out var idEl)
                ? idEl.GetString()?.Trim()
                : null;
            if (string.IsNullOrWhiteSpace(pointNumber))
                continue;

            var title = point.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : null;
            var content = point.TryGetProperty("text", out var textEl) ? textEl.GetString() : null;
            var section = point.TryGetProperty("section", out var sectionEl) ? sectionEl.GetString() : null;
            var pageHint = point.TryGetProperty("page_hint", out var pageEl) && pageEl.TryGetInt32(out var pageNo)
                ? pageNo + 1
                : (int?)null;
            var pageReference = !string.IsNullOrWhiteSpace(section)
                ? pageHint is > 0 ? $"{section} · p. {pageHint}" : section
                : pageHint is > 0 ? $"p. {pageHint}" : null;

            rows.Add(new RegulationExtractPointSeed(
                PointNumber: pointNumber,
                PointTitle: title,
                PointContent: content?.Trim() ?? "",
                PageReference: pageReference,
                IsIntroductionPoint: false,
                IsAnnexPoint: false));
        }

        return rows;
    }

    public List<RegulationExtractPointSeed> GetCachedTfsRegulationExtractPoints()
    {
        lock (RegulationExtractCacheLock)
        {
            TfsRegulationExtractCache ??= LoadTfsRegulationExtractPoints();
            return TfsRegulationExtractCache;
        }
    }

    public int GetTfsRegulationExtractPointCount() => GetCachedTfsRegulationExtractPoints().Count;

    public (List<string> ClauseNumbers, List<string> ClauseTitles) GetCbuaeClauseMatchTokens()
    {
        var numbers = new List<string>();
        var titles = new List<string>();
        foreach (var row in GetCachedCbuaeSeedJudgments())
        {
            if (!string.IsNullOrWhiteSpace(row.ClauseNo))
                numbers.Add(row.ClauseNo.Trim());
            if (!string.IsNullOrWhiteSpace(row.ClauseTitle))
                titles.Add(row.ClauseTitle.Trim());
        }
        return (numbers, titles);
    }

    public List<NdRegulationPoint> FilterRegulationPointsToCbuaeDemoScope(IReadOnlyList<NdRegulationPoint> points)
    {
        var judgments = GetCachedCbuaeSeedJudgments();
        var seedClauseKeys = BuildSeedClauseKeySet(judgments);
        var matched = new List<NdRegulationPoint>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in points)
        {
            if (string.IsNullOrWhiteSpace(p.PointNumber)) continue;
            var key = NormalizeClauseKey(p.PointNumber);
            if (!seedClauseKeys.Contains(key)) continue;

            if (used.Contains(key)) continue;
            used.Add(key);
            matched.Add(p);
        }

        return matched;
    }

    public List<T> FilterLitePointsToCbuaeDemoScope<T>(
        IReadOnlyList<T> points,
        Func<T, string?> pointNumber,
        Func<T, string?> pointTitle,
        Func<T, Guid> id)
    {
        var judgments = GetCachedCbuaeSeedJudgments();
        var seedClauseKeys = BuildSeedClauseKeySet(judgments);
        var matched = new List<T>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in points)
        {
            var pn = pointNumber(p);
            if (string.IsNullOrWhiteSpace(pn)) continue;
            var key = NormalizeClauseKey(pn);
            if (!seedClauseKeys.Contains(key)) continue;

            if (used.Contains(key)) continue;
            used.Add(key);
            matched.Add(p);
        }

        return matched;
    }

    public async Task<NdAnalysisRun> GetOrCreateDemoCbuaeRunAsync(Guid userId, CancellationToken ct = default)
    {
        var seedCount = GetCachedCbuaeSeedJudgments().Count;
        var existing = await db.NdAnalysisRuns
            .Include(r => r.Points)
            .Where(r => r.CreatedBy == userId
                && r.WorkflowEngine == AnalysisWorkflowEngine.RegulPipelineFull
                && r.Status != "deleted"
                && r.Description != null
                && r.Description.Contains("Arena judgments seeded"))
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (existing is { TotalPointsCount: > 0 } && existing.TotalPointsCount == seedCount)
            return existing;

        return await CreateDemoRegulRunFromJudgmentsAsync(
            userId,
            new DemoRegulSaveRequest(
                RegulationNameHint: "CBUAE_EN_3945",
                InternalNameHint: "290626",
                UseSeedFile: true),
            ct);
    }

    /// <summary>Applies CBUAE seed judgments to an existing draft/run (demo simulation).</summary>
    public async Task<int> ApplyCbuaeSeedJudgmentsToRunAsync(
        Guid runId,
        Guid userId,
        CancellationToken ct = default)
    {
        var judgments = LoadCbuaeSeedJudgments();
        if (judgments.Count == 0)
            throw new InvalidOperationException("No CBUAE seed judgments available.");

        var applied = await ApplyJudgmentRowsToRunAsync(
            runId,
            judgments,
            userId,
            preserveWorkflowStatus: false,
            ct);

        if (applied == 0)
            throw new InvalidOperationException("No run points matched CBUAE seed judgments by clause number.");

        return applied;
    }

    /// <summary>
    /// Refreshes a CBUAE demo run from analys1demo / seed template (gap text, CAP, landing message).
    /// Preserves workflow status (submitted_for_review, etc.) when used from gap-analysis GET.
    /// </summary>
    public async Task<int> SyncRegulDemoRunFromTemplateAsync(
        Guid runId,
        Guid? changedBy,
        bool preserveWorkflowStatus = true,
        CancellationToken ct = default)
    {
        var syncLock = DemoRunSyncLocks.GetOrAdd(runId, _ => new SemaphoreSlim(1, 1));
        var throttleKey = $"nd-demo-run-sync:{runId}";
        if (preserveWorkflowStatus && cache.TryGetValue(throttleKey, out _))
            return 0;

        // Gap-analysis GET /results must not queue behind an in-flight sync for the same run.
        if (preserveWorkflowStatus)
        {
            if (!syncLock.Wait(0))
                return 0;
        }
        else
        {
            await syncLock.WaitAsync(ct);
        }

        try
        {
            if (preserveWorkflowStatus && cache.TryGetValue(throttleKey, out _))
                return 0;

            var run = await db.NdAnalysisRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
            if (run == null || !AnalysisWorkflowEngine.IsRegulFamily(run.WorkflowEngine))
                return 0;
            if (!await IsCbuaeAmlDemoRunAsync(run, ct))
                return 0;

            var judgments = await demoWorkspace.LoadJudgmentsForRunAsync(run, ct);
            if (judgments.Count == 0)
                return 0;

            var actor = changedBy ?? run.CreatedBy ?? Guid.Empty;
            var applied = await ApplyJudgmentRowsToRunAsync(
                runId,
                judgments,
                actor,
                preserveWorkflowStatus,
                ct);

            if (preserveWorkflowStatus)
                cache.Set(throttleKey, true, DemoRunSyncThrottle);

            return applied;
        }
        finally
        {
            syncLock.Release();
        }
    }

    /// <summary>After admin template / seed file changes, refresh all CBUAE demo analysis runs.</summary>
    public async Task<int> SyncAllCbuaeDemoRunsFromTemplateAsync(CancellationToken ct = default)
    {
        var runIds = await db.NdAnalysisRuns.AsNoTracking()
            .Where(r =>
                r.WorkflowEngine == AnalysisWorkflowEngine.RegulPipelineFull
                && r.Status != "deleted"
                && r.TotalPointsCount > 0)
            .Select(r => r.Id)
            .ToListAsync(ct);

        var total = 0;
        foreach (var runId in runIds)
        {
            cache.Remove($"nd-demo-run-sync:{runId}");
            total += await SyncRegulDemoRunFromTemplateAsync(runId, null, preserveWorkflowStatus: true, ct);
        }

        return total;
    }

  private async Task<int> ApplyJudgmentRowsToRunAsync(
        Guid runId,
        List<DemoRegulJudgmentRow> judgments,
        Guid changedBy,
        bool preserveWorkflowStatus,
        CancellationToken ct)
    {
        var run = await db.NdAnalysisRuns
            .Include(r => r.Points)
            .FirstOrDefaultAsync(r => r.Id == runId, ct)
            ?? throw new InvalidOperationException("Analysis run not found.");

        var judgmentMap = BuildJudgmentLookup(judgments);
        var regPointIds = run.Points
            .Where(p => p.RegulationPointId.HasValue)
            .Select(p => p.RegulationPointId!.Value)
            .Distinct()
            .ToList();
        var regPointsById = regPointIds.Count == 0
            ? new Dictionary<Guid, NdRegulationPoint>()
            : await db.NdRegulationPoints.AsNoTracking()
                .Where(p => regPointIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, ct);

        var applied = 0;
        var pendingHistories = new List<(NdAnalysisPoint Point, string Cap)>();

        foreach (var point in run.Points)
        {
            var row = ResolveJudgmentForAnalysisPoint(point, judgmentMap, regPointsById);
            if (row == null) continue;

            var (clauseNo, clauseText) = ResolveClauseFromAnalysisPoint(point, regPointsById);
            var judgment = row.ToJudgmentResult();
            var landingMessage = NdRegulJudgmentFormatter.FormatLandingMessage(clauseNo, clauseText, judgment);

            NdRegulAnalysisPointSync.ApplyForwardJudgment(point, judgment, landingMessage);

            var finding = await db.NdRegulForwardFindings
                .FirstOrDefaultAsync(f => f.AnalysisRunId == run.Id && f.AnalysisPointId == point.Id, ct);
            if (finding == null)
            {
                db.NdRegulForwardFindings.Add(new NdRegulForwardFinding
                {
                    AnalysisRunId = run.Id,
                    AnalysisPointId = point.Id,
                    ClauseNo = clauseNo,
                    ClauseText = clauseText,
                    Status = "completed",
                    ResultJson = JsonSerializer.Serialize(judgment, JudgmentJsonOptions),
                });
            }
            else
            {
                finding.ClauseNo = clauseNo;
                finding.ClauseText = clauseText;
                finding.Status = "completed";
                finding.ResultJson = JsonSerializer.Serialize(judgment, JudgmentJsonOptions);
                finding.ErrorMessage = null;
                finding.UpdatedAt = DateTimeOffset.UtcNow;
            }

            applied++;
            var cap = point.LandingAiActionPlan?.Trim() ?? "";
            if (!string.IsNullOrWhiteSpace(cap))
                pendingHistories.Add((point, cap));
        }

        run.TotalPointsCount = run.Points.Count;
        if (preserveWorkflowStatus)
        {
            run.LandingAiCompletedCount = Math.Max(run.LandingAiCompletedCount, applied);
            run.ProcessedPointsCount = Math.Max(run.ProcessedPointsCount, applied);
        }
        else
        {
            run.RegulClausesConfirmedAt ??= DateTimeOffset.UtcNow;
            run.ProcessedPointsCount = applied;
            run.LandingAiCompletedCount = applied;
            run.DualVerifyCompletedCount = 0;
            run.DualVerifyFailedCount = 0;
            run.RegulPipelinePhase = "done";
            run.Status = "completed";
        }
        run.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);

        foreach (var (point, cap) in pendingHistories)
            await UpsertAiOriginalActionPlanHistoryAsync(point, cap, changedBy, ct);

        return applied;
    }

    private async Task UpsertAiOriginalActionPlanHistoryAsync(
        NdAnalysisPoint point,
        string cap,
        Guid changedBy,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cap))
            return;
        if (!string.IsNullOrWhiteSpace(point.FinalActionPlan?.Trim()))
            return;

        var current = await db.NdActionPlanHistories
            .Where(h => h.AnalysisPointId == point.Id && h.IsCurrent)
            .FirstOrDefaultAsync(ct);
        if (current != null
            && current.ChangeType == "ai_original"
            && string.Equals(current.ActionPlanContent?.Trim(), cap, StringComparison.Ordinal))
            return;

        var existing = await db.NdActionPlanHistories
            .Where(h => h.AnalysisPointId == point.Id && h.IsCurrent)
            .ToListAsync(ct);
        foreach (var h in existing)
            h.IsCurrent = false;

        var maxVer = await db.NdActionPlanHistories
            .Where(h => h.AnalysisPointId == point.Id)
            .MaxAsync(h => (int?)h.VersionNumber, ct) ?? 0;

        db.NdActionPlanHistories.Add(new NdActionPlanHistory
        {
            AnalysisPointId = point.Id,
            VersionNumber = maxVer + 1,
            ActionPlanContent = cap,
            ChangeType = "ai_original",
            IsCurrent = true,
            ChangedBy = changedBy == Guid.Empty ? null : changedBy,
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> IsCbuaeAmlDemoRunAsync(NdAnalysisRun run, CancellationToken ct = default)
    {
        var regIds = ParseGuidList(run.SelectedRegulationDocIds);
        var intIds = ParseGuidList(run.SelectedInternalDocIds);
        if (regIds.Count == 0 || intIds.Count == 0)
            return false;

        var regMatch = false;
        foreach (var id in regIds)
        {
            var name = await db.NdRegulationDocuments.AsNoTracking()
                .Where(d => d.Id == id)
                .Select(d => d.Name)
                .FirstOrDefaultAsync(ct);
            if (NameMatchesHint(name, "CBUAE") || NameMatchesHint(name, "3945"))
            {
                regMatch = true;
                break;
            }
        }

        var intMatch = false;
        foreach (var id in intIds)
        {
            var doc = await db.StoredDocuments.AsNoTracking()
                .Where(d => d.Id == id)
                .Select(d => new { d.OriginalFileName, d.Title })
                .FirstOrDefaultAsync(ct);
            if (doc != null
                && (NameMatchesHint(doc.OriginalFileName, "290626") || NameMatchesHint(doc.Title, "290626"))
                && (NameMatchesHint(doc.OriginalFileName, "AML") || NameMatchesHint(doc.Title, "AML")))
            {
                intMatch = true;
                break;
            }
        }

        return regMatch && intMatch;
    }

    private static List<Guid> ParseGuidList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            var raw = JsonSerializer.Deserialize<List<string>>(json) ?? [];
            var list = new List<Guid>();
            foreach (var s in raw)
            {
                if (Guid.TryParse(s, out var g)) list.Add(g);
            }
            return list;
        }
        catch
        {
            return [];
        }
    }

    public static DemoRegulJudgmentRow? ResolveJudgmentForAnalysisPoint(
        NdAnalysisPoint point,
        Dictionary<string, DemoRegulJudgmentRow> judgmentMap,
        IReadOnlyDictionary<Guid, NdRegulationPoint> regPointsById)
    {
        if (point.RegulationPointId is Guid regId && regPointsById.TryGetValue(regId, out var regPoint))
        {
            if (!string.IsNullOrWhiteSpace(regPoint.PointNumber)
                && judgmentMap.TryGetValue(NormalizeClauseKey(regPoint.PointNumber), out var byNum))
                return byNum;
            if (!string.IsNullOrWhiteSpace(regPoint.PointTitle)
                && judgmentMap.TryGetValue(NormalizeClauseKey(regPoint.PointTitle), out var byTitle))
                return byTitle;
        }

        var (clauseNo, _) = ParseClauseFromSnapshot(point.PointSnapshot);
        if (!string.IsNullOrWhiteSpace(clauseNo)
            && judgmentMap.TryGetValue(NormalizeClauseKey(clauseNo), out var bySnapshot))
            return bySnapshot;

        if (!string.IsNullOrWhiteSpace(point.PointSnapshot))
        {
            try
            {
                using var doc = JsonDocument.Parse(point.PointSnapshot);
                var root = doc.RootElement;
                var title = root.TryGetProperty("pointTitle", out var pt) ? pt.GetString() : null;
                if (!string.IsNullOrWhiteSpace(title)
                    && judgmentMap.TryGetValue(NormalizeClauseKey(title), out var byPointTitle))
                    return byPointTitle;
            }
            catch { /* ignore */ }
        }

        return null;
    }

    public static (string ClauseNo, string ClauseText) ResolveClauseFromAnalysisPoint(
        NdAnalysisPoint point,
        IReadOnlyDictionary<Guid, NdRegulationPoint> regPointsById)
    {
        var (snapNo, snapText) = ParseClauseFromSnapshot(point.PointSnapshot);
        if (point.RegulationPointId is Guid regId && regPointsById.TryGetValue(regId, out var regPoint))
        {
            var regNo = regPoint.PointNumber?.Trim() ?? "";
            if (!string.IsNullOrWhiteSpace(snapNo)
                && !string.Equals(snapNo, regNo, StringComparison.OrdinalIgnoreCase))
                return (snapNo, snapText);
            return (regNo, regPoint.PointContent ?? "");
        }

        return (snapNo, snapText);
    }

    private static (string ClauseNo, string ClauseText) ParseClauseFromSnapshot(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return ("", "");
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var no = root.TryGetProperty("pointNumber", out var pn) ? pn.GetString() ?? ""
                : root.TryGetProperty("point_number", out var pn2) ? pn2.GetString() ?? "" : "";
            var text = root.TryGetProperty("pointContent", out var pc) ? pc.GetString() ?? ""
                : root.TryGetProperty("pointText", out var pt) ? pt.GetString() ?? ""
                : root.TryGetProperty("point_text", out var pt2) ? pt2.GetString() ?? ""
                : root.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
            return (no, text);
        }
        catch
        {
            return ("", "");
        }
    }

    /// <summary>Creates a new completed demo run from the UI session (one row per demo completion).</summary>
    public async Task<NdAnalysisRun> CreateDemoRunFromSessionAsync(
        Guid userId,
        DemoSaveRequest body,
        CancellationToken ct = default)
    {
        var inputs = body.Points?
            .Where(p => !string.IsNullOrWhiteSpace(p.PointId))
            .Where(p => !string.IsNullOrWhiteSpace(p.LandingMessage) || !string.IsNullOrWhiteSpace(p.LlmMessage))
            .ToList() ?? [];
        if (inputs.Count == 0)
            throw new InvalidOperationException("No demo point results to save.");

        var run = new NdAnalysisRun
        {
            Name = string.IsNullOrWhiteSpace(body.Name)
                ? $"[Demo] Gap analysis {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC"
                : body.Name.Trim(),
            Description = "Demonstration run — results from seeded DB, no AI credits used.",
            ComplianceSessionId = SeededComplianceSessionId,
            Status = "completed",
            SelectedPointsSnapshot = JsonSerializer.Serialize(body.SelectedPointsSnapshot ?? []),
            SelectedInternalDocIds = JsonSerializer.Serialize(body.SelectedInternalDocIds ?? []),
            SelectedRegulationDocIds = JsonSerializer.Serialize(body.SelectedRegulationDocIds ?? []),
            CreatedBy = userId,
        };
        db.NdAnalysisRuns.Add(run);

        var (stats, pendingHistories) = AddDemoPoints(run, userId, inputs, regDocId: null);
        ApplyRunStats(run, stats);

        await db.SaveChangesAsync(ct);

        foreach (var (point, cap, changedBy) in pendingHistories)
        {
            db.NdActionPlanHistories.Add(new NdActionPlanHistory
            {
                AnalysisPointId = point.Id,
                VersionNumber = 1,
                ActionPlanContent = cap,
                ChangeType = "ai_original",
                IsCurrent = true,
                ChangedBy = changedBy,
            });
        }

        if (pendingHistories.Count > 0)
            await db.SaveChangesAsync(ct);

        return run;
    }

    public async Task<NdAnalysisRun> GetOrCreateDemoRunAsync(Guid userId, CancellationToken ct = default)
    {
        var existing = await db.NdAnalysisRuns
            .Include(r => r.Points)
            .FirstOrDefaultAsync(
                r => r.ComplianceSessionId == SeededComplianceSessionId
                     && r.CreatedBy == userId
                     && r.Name.StartsWith("[Demo]"),
                ct);
        if (existing != null) return existing;

        var session = await db.ComplianceSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == SeededComplianceSessionId, ct)
            ?? throw new InvalidOperationException(
                "Demo compliance session not found. Run bundle seed or migration first.");

        var results = JsonSerializer.Deserialize<List<JsonElement>>(session.ResultsJson) ?? [];
        if (results.Count == 0)
            throw new InvalidOperationException("Demo compliance session has no point results.");

        var (regDocId, intDocId) = ParseLinkedDocIds(session.SummaryJson);

        var run = new NdAnalysisRun
        {
            Name = "[Demo] TFS × IMPTFS gap analysis",
            Description = "Demonstration run — results from seeded DB, no AI credits used.",
            ComplianceSessionId = SeededComplianceSessionId,
            Status = "completed",
            TotalPointsCount = 0,
            ProcessedPointsCount = 0,
            LandingAiCompletedCount = 0,
            DualVerifyCompletedCount = 0,
            DualVerifyFailedCount = 0,
            SelectedInternalDocIds = JsonSerializer.Serialize(intDocId.HasValue ? new[] { intDocId.Value.ToString() } : Array.Empty<string>()),
            SelectedRegulationDocIds = JsonSerializer.Serialize(regDocId.HasValue ? new[] { regDocId.Value.ToString() } : Array.Empty<string>()),
            CreatedBy = userId,
        };
        db.NdAnalysisRuns.Add(run);

        var seedInputs = new List<DemoPointInput>();
        foreach (var row in results)
        {
            var pointId = row.TryGetProperty("point_id", out var pid) ? pid.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(pointId)) continue;

            var title = row.TryGetProperty("title", out var t) ? t.GetString() : null;
            var text = row.TryGetProperty("text", out var tx) ? tx.GetString() : null;
            var landing = ReadString(row, "landingMessage", "message") ?? "";
            var llm = ReadString(row, "llmMessage") ?? "";
            JsonElement? agreementEl = row.TryGetProperty("agreementJson", out var aj) && aj.ValueKind == JsonValueKind.Object
                ? aj
                : null;

            seedInputs.Add(new DemoPointInput(
                pointId,
                title,
                text,
                landing,
                llm,
                agreementEl.HasValue ? JsonSerializer.Deserialize<object>(agreementEl.Value.GetRawText()) : null));
        }

        var (stats, pendingHistories) = AddDemoPoints(run, userId, seedInputs, regDocId);
        ApplyRunStats(run, stats);

        await db.SaveChangesAsync(ct);

        foreach (var (point, cap, changedBy) in pendingHistories)
        {
            db.NdActionPlanHistories.Add(new NdActionPlanHistory
            {
                AnalysisPointId = point.Id,
                VersionNumber = 1,
                ActionPlanContent = cap,
                ChangeType = "ai_original",
                IsCurrent = true,
                ChangedBy = changedBy,
            });
        }

        if (pendingHistories.Count > 0)
            await db.SaveChangesAsync(ct);

        return run;
    }

    private sealed record DemoPointStats(int Saved, int LandingDone, int DualDone, int DualFailed);

    private (DemoPointStats Stats, List<(NdAnalysisPoint Point, string Cap, Guid ChangedBy)> PendingHistories) AddDemoPoints(
        NdAnalysisRun run,
        Guid userId,
        IReadOnlyList<DemoPointInput> inputs,
        Guid? regDocId)
    {
        var landingDone = 0;
        var dualDone = 0;
        var dualFailed = 0;
        var savedPoints = 0;
        var pendingHistories = new List<(NdAnalysisPoint Point, string Cap, Guid ChangedBy)>();

        foreach (var input in inputs)
        {
            var pointId = input.PointId.Trim();
            var landing = input.LandingMessage ?? "";
            var llm = input.LlmMessage ?? "";

            DualVerifyAgreementDto? agreement = null;
            if (input.AgreementJson != null)
            {
                var agreementJson = JsonSerializer.Serialize(input.AgreementJson);
                agreement = JsonSerializer.Deserialize<DualVerifyAgreementDto>(agreementJson);
            }
            else if (!string.IsNullOrWhiteSpace(landing) && !string.IsNullOrWhiteSpace(llm))
            {
                agreement = NdComplianceParser.ComparePasses(landing, llm);
            }

            var landingStatus = ClampPhaseStatus(string.IsNullOrWhiteSpace(landing)
                ? "failed"
                : NdComplianceParser.ExtractStatusFromMessage(landing));
            if (landingStatus != "failed") landingDone++;

            var googleStatus = ClampPhaseStatus(string.IsNullOrWhiteSpace(llm)
                ? (string.IsNullOrWhiteSpace(landing) ? "failed" : "pending")
                : NdComplianceParser.ExtractStatusFromMessage(llm));

            var dualStatus = agreement == null
                ? (string.IsNullOrWhiteSpace(llm) ? "skipped" : "pending")
                : string.Equals(agreement.Status, "aligned", StringComparison.OrdinalIgnoreCase)
                    ? "passed"
                    : "failed";

            if (dualStatus == "passed") dualDone++;
            else if (dualStatus == "failed") dualFailed++;

            var finalStatus = !string.IsNullOrWhiteSpace(landing)
                ? NdComplianceParser.ExtractStatusFromMessage(landing)
                : null;
            if (agreement != null && !string.Equals(agreement.Status, "aligned", StringComparison.OrdinalIgnoreCase))
            {
                var llmSt = NdComplianceParser.ExtractStatusFromMessage(llm);
                if (!string.IsNullOrWhiteSpace(llm)) finalStatus = llmSt;
            }
            finalStatus = ClampFinalStatus(finalStatus);

            var cap = NdComplianceParser.ExtractActionPlan(landing) ?? NdComplianceParser.ExtractActionPlan(llm);
            var now = DateTimeOffset.UtcNow;

            var point = new NdAnalysisPoint
            {
                AnalysisRunId = run.Id,
                PointSnapshot = JsonSerializer.Serialize(new
                {
                    pointNumber = pointId,
                    pointTitle = input.Title,
                    pointContent = input.Text,
                    regulationDocumentId = regDocId?.ToString(),
                }),
                LandingAiStatus = landingStatus,
                LandingAiResult = string.IsNullOrWhiteSpace(landing) ? null : JsonSerializer.Serialize(new { message = landing, agreement }),
                LandingAiActionPlan = cap,
                LandingAiRunAt = string.IsNullOrWhiteSpace(landing) ? null : now,
                GoogleAiStatus = googleStatus,
                GoogleAiResult = string.IsNullOrWhiteSpace(llm) ? null : JsonSerializer.Serialize(new { message = llm, agreement }),
                GoogleAiRunAt = string.IsNullOrWhiteSpace(llm) ? null : now,
                DualVerifyStatus = dualStatus,
                DualVerifyRunAt = agreement != null ? now : null,
                FinalStatus = finalStatus,
                FinalActionPlan = cap,
                OriginalAiActionPlan = cap,
            };
            run.Points.Add(point);
            savedPoints++;

            if (!string.IsNullOrWhiteSpace(cap))
                pendingHistories.Add((point, cap, userId));
        }

        return (new DemoPointStats(savedPoints, landingDone, dualDone, dualFailed), pendingHistories);
    }

    private static string ClampPhaseStatus(string status) => status switch
    {
        "pending" or "running" or "compliant" or "partial_compliant" or "non_compliant" or "failed" => status,
        _ => "non_compliant",
    };

    private static string? ClampFinalStatus(string? status) => status switch
    {
        null or "" => null,
        "compliant" or "partial_compliant" or "non_compliant" => status,
        _ => NdComplianceParser.NormalizeStatus(status),
    };

    private static void ApplyRunStats(NdAnalysisRun run, DemoPointStats stats)
    {
        run.TotalPointsCount = stats.Saved;
        run.ProcessedPointsCount = stats.Saved;
        run.LandingAiCompletedCount = stats.LandingDone;
        run.DualVerifyCompletedCount = stats.DualDone;
        run.DualVerifyFailedCount = stats.DualFailed;
        if (stats.DualFailed > 0) run.Status = "dual_verify_failed";
    }

    private static string? ReadString(JsonElement row, params string[] names)
    {
        foreach (var name in names)
        {
            if (row.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String)
            {
                var s = el.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
        }
        return null;
    }

    private static (Guid? RegDocId, Guid? InternalDocId) ParseLinkedDocIds(string? summaryJson)
    {
        if (string.IsNullOrWhiteSpace(summaryJson)) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(summaryJson);
            var root = doc.RootElement;
            Guid? reg = root.TryGetProperty("linkedGovDocumentId", out var g) && Guid.TryParse(g.GetString(), out var rg) ? rg : null;
            Guid? internal_ = root.TryGetProperty("linkedInternalDocumentId", out var i) && Guid.TryParse(i.GetString(), out var ri) ? ri : null;
            return (reg, internal_);
        }
        catch
        {
            return (null, null);
        }
    }

    private async Task<Guid> ResolveRegulationDocumentIdAsync(
        Guid userId,
        DemoRegulSaveRequest body,
        CancellationToken ct)
    {
        if (Guid.TryParse(body.RegulationDocumentId, out var explicitId))
        {
            var exists = await db.NdRegulationDocuments.AsNoTracking()
                .AnyAsync(d => d.Id == explicitId, ct);
            if (exists) return explicitId;
        }

        var hint = body.RegulationNameHint ?? "CBUAE_EN_3945";
        var candidates = await db.NdRegulationDocuments.AsNoTracking()
            .Where(d => d.ExtractionStatus == "completed")
            .OrderByDescending(d => d.CreatedBy == userId)
            .ThenByDescending(d => d.ExtractedAt ?? d.UpdatedAt)
            .ToListAsync(ct);

        var match = candidates.FirstOrDefault(d => d.CreatedBy == userId && NameMatchesHint(d.Name, hint))
            ?? candidates.FirstOrDefault(d => NameMatchesHint(d.Name, hint))
            ?? candidates.FirstOrDefault(d => NameMatchesHint(d.Name, "3945"))
            ?? candidates.FirstOrDefault(d => NameMatchesHint(d.Name, "CBUAE"));
        if (match == null)
            throw new InvalidOperationException(
                "Regulation document not found. Upload and extract CBUAE_EN_3945_VER2 or pass regulationDocumentId.");

        return match.Id;
    }

    private async Task<Guid> ResolveInternalDocumentIdAsync(
        Guid userId,
        DemoRegulSaveRequest body,
        CancellationToken ct)
    {
        if (Guid.TryParse(body.InternalDocumentId, out var explicitId))
        {
            var exists = await db.StoredDocuments.AsNoTracking()
                .AnyAsync(d => d.Id == explicitId, ct);
            if (exists) return explicitId;
        }

        var hint = body.InternalNameHint ?? "290626";
        var candidates = await db.StoredDocuments.AsNoTracking()
            .Where(d => d.ParseStatus == "parsed"
                && (d.DocKind == "document" || d.DocKind == "internal"))
            .OrderByDescending(d => d.UploadedBy == userId)
            .ThenByDescending(d => d.ParsedAt ?? d.UpdatedAt)
            .ToListAsync(ct);

        var match = candidates.FirstOrDefault(d =>
                d.UploadedBy == userId
                && (NameMatchesHint(d.OriginalFileName, hint) || NameMatchesHint(d.Title, hint)))
            ?? candidates.FirstOrDefault(d =>
                NameMatchesHint(d.OriginalFileName, hint) || NameMatchesHint(d.Title, hint))
            ?? candidates.FirstOrDefault(d =>
                NameMatchesHint(d.OriginalFileName, "AML") && NameMatchesHint(d.OriginalFileName, "290626"));
        if (match == null)
            throw new InvalidOperationException(
                "Internal document not found. Upload and parse Internal AML Manual 290626 or pass internalDocumentId.");

        return match.Id;
    }

    private static bool NameMatchesHint(string? name, string hint)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(hint))
            return false;
        var normalizedName = NormalizeDocName(name);
        var normalizedHint = NormalizeDocName(hint);
        return normalizedName.Contains(normalizedHint, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsCbuaeRegulationName(string? name) =>
        NameMatchesHint(name, "CBUAE") || NameMatchesHint(name, "3945");

    public static bool IsImptfsInternalName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        var normalized = NormalizeDocName(name);
        return normalized.Contains("IMPTFS", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>TFS Guidelines regulation — not IMPTFS internal policy.</summary>
    public static bool IsTfsRegulationName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || IsImptfsInternalName(name))
            return false;
        var normalized = NormalizeDocName(name);
        return normalized.Contains("TFS", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDocName(string value) =>
        string.Concat(value.Where(c => !char.IsWhiteSpace(c)));

    public static string NormalizeDocNameForMatch(string value) => NormalizeDocName(value);

    public static Dictionary<string, DemoRegulJudgmentRow> BuildJudgmentLookup(IReadOnlyList<DemoRegulJudgmentRow> rows)
    {
        var map = new Dictionary<string, DemoRegulJudgmentRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.ClauseNo)) continue;
            map[NormalizeClauseKey(row.ClauseNo)] = row;
            if (!string.IsNullOrWhiteSpace(row.ClauseTitle))
                map[NormalizeClauseKey(row.ClauseTitle)] = row;
        }
        return map;
    }

    /// <summary>Exact seed clause numbers only — avoids parent rows matching shared titles.</summary>
    public static HashSet<string> BuildSeedClauseKeySet(IReadOnlyList<DemoRegulJudgmentRow> rows)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.ClauseNo)) continue;
            set.Add(NormalizeClauseKey(row.ClauseNo));
        }
        return set;
    }

    private static List<(NdRegulationPoint? RegPoint, DemoRegulJudgmentRow Row)> MatchAllJudgmentsToRegulationPoints(
        IReadOnlyList<NdRegulationPoint> regPoints,
        IReadOnlyList<DemoRegulJudgmentRow> judgments)
    {
        var regByKey = new Dictionary<string, NdRegulationPoint>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in regPoints)
        {
            if (!string.IsNullOrWhiteSpace(p.PointNumber))
            {
                var numKey = NormalizeClauseKey(p.PointNumber);
                if (!string.IsNullOrWhiteSpace(numKey) && !regByKey.ContainsKey(numKey))
                    regByKey[numKey] = p;
            }
            if (!string.IsNullOrWhiteSpace(p.PointTitle))
            {
                var titleKey = NormalizeClauseKey(p.PointTitle);
                if (!string.IsNullOrWhiteSpace(titleKey) && !regByKey.ContainsKey(titleKey))
                    regByKey[titleKey] = p;
            }
        }

        var matched = new List<(NdRegulationPoint?, DemoRegulJudgmentRow)>();
        foreach (var row in judgments)
        {
            if (string.IsNullOrWhiteSpace(row.ClauseNo)) continue;
            var key = NormalizeClauseKey(row.ClauseNo);
            NdRegulationPoint? regPoint = regByKey.GetValueOrDefault(key);
            if (regPoint == null && TryGetParentClauseKey(key, out var parentKey))
                regPoint = regByKey.GetValueOrDefault(parentKey);
            matched.Add((regPoint, row));
        }

        return matched;
    }

    private static bool TryGetParentClauseKey(string clauseKey, out string parentKey)
    {
        parentKey = "";
        var dash = clauseKey.LastIndexOf('-');
        if (dash <= 0 || dash >= clauseKey.Length - 1) return false;
        var suffix = clauseKey[(dash + 1)..];
        if (suffix.Length != 1 || !char.IsLetter(suffix[0])) return false;
        parentKey = clauseKey[..dash];
        return !string.IsNullOrWhiteSpace(parentKey);
    }

    private static List<(NdRegulationPoint RegPoint, DemoRegulJudgmentRow Row)> MatchJudgmentsToRegulationPoints(
        IReadOnlyList<NdRegulationPoint> regPoints,
        Dictionary<string, DemoRegulJudgmentRow> judgmentMap)
    {
        var matched = new List<(NdRegulationPoint, DemoRegulJudgmentRow)>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var regPoint in regPoints)
        {
            DemoRegulJudgmentRow? row = null;
            if (!string.IsNullOrWhiteSpace(regPoint.PointNumber))
                row = judgmentMap.GetValueOrDefault(NormalizeClauseKey(regPoint.PointNumber));
            if (row == null && !string.IsNullOrWhiteSpace(regPoint.PointTitle))
                row = judgmentMap.GetValueOrDefault(NormalizeClauseKey(regPoint.PointTitle));
            if (row == null) continue;

            var key = NormalizeClauseKey(regPoint.PointNumber ?? regPoint.PointTitle ?? regPoint.Id.ToString());
            if (used.Contains(key)) continue;
            used.Add(key);
            matched.Add((regPoint, row));
        }

        return matched;
    }

    private static string NormalizeClauseKey(string value)
    {
        var trimmed = value.Trim();
        while (trimmed.EndsWith('.'))
            trimmed = trimmed[..^1];
        return trimmed;
    }
}
