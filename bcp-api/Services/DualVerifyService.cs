using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.Entities;
using Reguliq.Api.Infrastructure;
using Reguliq.Api.Models;
using Reguliq.Api.Workers;

namespace Reguliq.Api.Services;

public class DualVerifyService(
    GovPointsService govPoints,
    DualVerifyStoreService store,
    CompliancePdfResolver compliancePdf,
    DualVerifyJobStageTracker stageTracker,
    SessionCancellationTracker cancellationTracker,
    LocalJobQueue queue,
    KafkaConfig kafkaConfig,
    KafkaProducerService kafka,
    DatabaseConfig dbConfig,
    AppDbContext db,
    IConfiguration config,
    IWebHostEnvironment env,
    ILogger<DualVerifyService> logger)
{
    private const string GovFileHash = "c84713f9aacd18415680356aeae47bcacff9c17458b5595b575400b12fe8f2ff";
    private const string InternalFileHash = "6a0a0bd13c7a32ea10c43c9a8391347a7e0caceaa0b17dd6443e9ee622111717";

    public async Task<DualVerifyHealthDto> GetHealthAsync(CancellationToken ct = default)
    {
        var tablesReady = await store.TablesReadyAsync();
        var kafkaConfigured = kafkaConfig.IsKafkaConfigured();
        var transport = kafkaConfig.GetTransportMode();
        var mode = !tablesReady ? "memory"
            : dbConfig.UsePostgres ? "supabase"
            : "file";

        return new DualVerifyHealthDto(
            "ok",
            transport,
            kafkaConfigured,
            new KafkaTopicsDto(
                config["KAFKA_TOPIC_JOBS"] ?? "dual-verify-jobs",
                config["KAFKA_TOPIC_RETRY"] ?? "dual-verify-retry",
                config["KAFKA_TOPIC_DLQ"] ?? "dual-verify-dlq",
                config["KAFKA_TOPIC_RESULTS"] ?? "dual-verify-results"),
            new PersistenceDto(
                tablesReady, tablesReady,
                !dbConfig.RequireSupabase && Directory.Exists(store.DataDir),
                store.DataDir, mode,
                mode == "memory"
                    ? PostgresConnectionDiagnostics.LastError
                      ?? "Database not connected — set ConnectionStrings:PostgreSQL or Supabase:Db* in appsettings.Development.json"
                    : null));
    }

    public async Task<DualVerifySession> CreateJobAsync(
        CreateDualVerifyJobRequest request,
        byte[]? internalPdf,
        string? internalFileName,
        IReadOnlyList<GovPoint>? clientGovPoints = null,
        Guid? internalStoredDocumentId = null,
        CancellationToken ct = default)
    {
        await AssertPersistenceReadyAsync(ct);

        var filtered = govPoints.ResolveSelectedPoints(
            request.PointIds,
            request.Granularity,
            clientGovPoints);
        if (filtered.Count == 0)
            throw new InvalidOperationException("No matching gov points selected");

        var compliance = await compliancePdf.ResolveForJobAsync(
            internalPdf,
            internalStoredDocumentId,
            request.InternalDocId,
            internalFileName,
            ct);
        internalPdf = compliance.PdfBytes ?? internalPdf;

        var sessionId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var transport = kafkaConfig.GetTransportMode();
        var maxAttempts = kafkaConfig.GetMaxAttempts();

        var session = new DualVerifySession
        {
            Id = sessionId,
            Status = "queued",
            Granularity = request.Granularity,
            GovDocId = request.GovDocId,
            InternalDocId = compliance.InternalDocId,
            GovFileHash = GovFileHash,
            InternalFileHash = compliance.FileHash,
            GovFileName = "TFS Guidelines.pdf",
            InternalFileName = compliance.FileName,
            TotalPoints = filtered.Count,
            QueuedPoints = filtered.Count,
            Phase2Model = request.Phase2Model,
            Transport = transport,
            CreatedAt = now,
            UpdatedAt = now
        };

        if (internalPdf is { Length: > 0 })
            store.SetInternalPdf(sessionId, internalPdf);

        await store.SaveSessionAsync(session, ct);

        var messages = new List<DualVerifyJobMessage>();

        foreach (var point in filtered)
        {
            var jobId = Guid.NewGuid();
            var pointJob = new DualVerifyPointJob
            {
                Id = jobId,
                SessionId = sessionId,
                PointId = point.PointId,
                PointTitle = point.Title,
                GovText = point.Text,
                Status = "queued",
                MaxAttempts = maxAttempts,
                CreatedAt = now,
                UpdatedAt = now
            };
            await store.SavePointJobAsync(pointJob, ct);

            messages.Add(new DualVerifyJobMessage(
                Guid.NewGuid().ToString(), jobId, sessionId,
                point.PointId, point.Title, point.Text,
                request.Granularity, request.GovDocId, compliance.InternalDocId,
                GovFileHash, compliance.FileHash,
                session.GovFileName ?? "", session.InternalFileName ?? "",
                request.Phase2Model, 1, maxAttempts,
                request.ForceRefresh, sessionId.ToString(), now,
                Transport: transport));
        }

        await EnqueueJobsAsync(messages, ct);

        logger.LogInformation("Created dual verify session {SessionId} with {Count} points ({Transport})",
            sessionId, filtered.Count, transport);
        return session;
    }

    private async Task EnqueueJobsAsync(List<DualVerifyJobMessage> messages, CancellationToken ct)
    {
        if (kafkaConfig.IsEnabled() && kafka.IsReady)
        {
            foreach (var msg in messages)
                await kafka.PublishJobAsync(msg, ct);

            if (env.IsDevelopment())
            {
                foreach (var msg in messages)
                    queue.Enqueue(msg);
            }
        }
        else
        {
            foreach (var msg in messages)
                queue.Enqueue(msg);
        }
    }

    public async Task<SessionProgressDto?> GetProgressAsync(Guid sessionId, CancellationToken ct = default)
    {
        await store.UpdateSessionCountsAsync(sessionId, ct);
        var session = await store.GetSessionAsync(sessionId, ct);
        if (session == null) return null;
        var run = await db.DocumentAnalysisRuns.AsNoTracking()
            .FirstOrDefaultAsync(r => r.DualVerifySessionId == sessionId, ct);
        var progress = MapProgress(session, run);
        var points = progress.Points.Select(p =>
        {
            if (p.Status != "running") return p;
            return stageTracker.TryGet(sessionId, p.PointId, out var stage)
                ? p with { RunningStage = stage }
                : p;
        }).ToList();
        return progress with { Points = points };
    }

    public async Task<List<PointJobDto>> GetResultsAsync(Guid sessionId, CancellationToken ct = default)
    {
        var session = await store.GetSessionAsync(sessionId, ct);
        if (session == null) return [];
        return session.PointJobs
            .Where(p => p.Status is "completed" or "failed")
            .OrderBy(p => p.PointId)
            .Select(p => new PointJobDto(
                p.Id, p.PointId, p.PointTitle, p.Status,
                p.LandingMessage, p.LlmMessage,
                DualVerifyAgreementService.FromJson(p.AgreementJson),
                p.ErrorMessage))
            .ToList();
    }

    /// <summary>Stop queued work and signal workers to skip remaining AI passes.</summary>
    public async Task<bool> CancelSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        var session = await store.GetSessionAsync(sessionId, ct);
        if (session == null) return false;

        if (session.Status is "completed" or "failed" or "cancelled")
            return true;

        cancellationTracker.MarkCancelled(sessionId);
        session.Status = "cancelled";
        session.UpdatedAt = DateTime.UtcNow;
        session.CompletedAt ??= DateTime.UtcNow;

        foreach (var job in session.PointJobs.Where(p => p.Status is "queued"))
        {
            job.Status = "cancelled";
            job.ErrorMessage = "Cancelled by user";
            job.UpdatedAt = DateTime.UtcNow;
            await store.SavePointJobAsync(job, ct);
        }

        await store.SaveSessionAsync(session, ct);
        await store.UpdateSessionCountsAsync(sessionId, ct);
        logger.LogInformation("Cancelled dual verify session {SessionId}", sessionId);
        return true;
    }

    /// <summary>Permanently remove a session and all point jobs from the database.</summary>
    public async Task<bool> DeleteSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        var session = await store.GetSessionAsync(sessionId, ct);
        if (session == null) return false;

        if (session.Status is "processing" or "queued")
            await CancelSessionAsync(sessionId, ct);

        cancellationTracker.MarkCancelled(sessionId);
        return await store.DeleteSessionAsync(sessionId, ct);
    }

    public static SessionProgressDto MapProgress(DualVerifySession session, DocumentAnalysisRun? run = null) =>
        new(
            new DualVerifySessionDto(
                session.Id, session.Status, session.TotalPoints,
                session.CompletedPoints, session.FailedPoints,
                session.RunningPoints, session.QueuedPoints,
                session.Transport, session.Phase2Model ?? "",
                session.Granularity, session.UpdatedAt,
                session.GovFileName,
                session.InternalFileName,
                session.GovFileHash,
                session.InternalFileHash,
                run?.RegulationDocumentId,
                run?.InternalDocumentId),
            session.PointJobs.OrderBy(p => p.PointId).Select(p => new PointJobDto(
                p.Id, p.PointId, p.PointTitle, p.Status,
                p.LandingMessage, p.LlmMessage,
                DualVerifyAgreementService.FromJson(p.AgreementJson),
                p.ErrorMessage)).ToList());

    public async Task<int> RetryFailedAsync(
        Guid sessionId,
        byte[]? internalPdf = null,
        CancellationToken ct = default)
    {
        var session = await store.GetSessionAsync(sessionId, ct);
        if (session == null) return 0;
        var failedIds = session.PointJobs
            .Where(p => p.Status == "failed")
            .Select(p => p.PointId)
            .ToList();
        return await RetryPointsAsync(sessionId, failedIds, null, internalPdf, forceRefresh: false, ct);
    }

    /// <summary>
    /// Re-queue specific points in an existing session. Existing jobs are reset;
    /// missing point IDs are created and appended (e.g. run remaining not-yet-analysed points).
    /// Same Kafka message shape as CreateJob / RetryFailed.
    /// </summary>
    public async Task<int> RetryPointsAsync(
        Guid sessionId,
        IReadOnlyList<string> pointIds,
        IReadOnlyList<GovPoint>? clientGovPoints = null,
        byte[]? internalPdf = null,
        bool forceRefresh = false,
        CancellationToken ct = default)
    {
        var session = await store.GetSessionAsync(sessionId, ct);
        if (session == null) return 0;
        if (pointIds.Count == 0) return 0;

        if (internalPdf is { Length: > 0 })
            store.SetInternalPdf(sessionId, internalPdf);

        cancellationTracker.Clear(sessionId);

        var transport = kafkaConfig.GetTransportMode();
        var maxAttempts = kafkaConfig.GetMaxAttempts();
        var now = DateTime.UtcNow;
        var byId = session.PointJobs.ToDictionary(p => p.PointId, StringComparer.OrdinalIgnoreCase);
        var messages = new List<DualVerifyJobMessage>();
        var added = 0;

        // Resolve only IDs that need a new job
        var missingIds = pointIds
            .Where(id => !string.IsNullOrWhiteSpace(id) && !byId.ContainsKey(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var resolvedNew = missingIds.Count == 0
            ? []
            : govPoints.ResolveSelectedPoints(missingIds, session.Granularity, clientGovPoints);
        var resolvedMap = resolvedNew.ToDictionary(p => p.PointId, StringComparer.OrdinalIgnoreCase);

        foreach (var rawId in pointIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(rawId)) continue;

            if (byId.TryGetValue(rawId, out var job))
            {
                if (job.Status is "running" or "queued")
                    continue;

                job.Status = "queued";
                job.Attempt = 1;
                job.ErrorMessage = null;
                job.UpdatedAt = now;
                if (forceRefresh)
                {
                    job.LandingMessage = null;
                    job.LlmMessage = null;
                    job.AgreementJson = null;
                }
                await store.SavePointJobAsync(job, ct);

                messages.Add(new DualVerifyJobMessage(
                    Guid.NewGuid().ToString(), job.Id, sessionId,
                    job.PointId, job.PointTitle, job.GovText,
                    session.Granularity, session.GovDocId, session.InternalDocId,
                    session.GovFileHash, session.InternalFileHash,
                    session.GovFileName ?? "", session.InternalFileName ?? "",
                    session.Phase2Model ?? "gemini-3.5-flash",
                    1, job.MaxAttempts, forceRefresh, sessionId.ToString(), now,
                    Transport: transport));
                continue;
            }

            if (!resolvedMap.TryGetValue(rawId, out var point))
                continue;

            var jobId = Guid.NewGuid();
            var pointJob = new DualVerifyPointJob
            {
                Id = jobId,
                SessionId = sessionId,
                PointId = point.PointId,
                PointTitle = point.Title,
                GovText = point.Text,
                Status = "queued",
                MaxAttempts = maxAttempts,
                CreatedAt = now,
                UpdatedAt = now,
            };
            await store.SavePointJobAsync(pointJob, ct);
            added++;

            messages.Add(new DualVerifyJobMessage(
                Guid.NewGuid().ToString(), jobId, sessionId,
                point.PointId, point.Title, point.Text,
                session.Granularity, session.GovDocId, session.InternalDocId,
                session.GovFileHash, session.InternalFileHash,
                session.GovFileName ?? "", session.InternalFileName ?? "",
                session.Phase2Model ?? "gemini-3.5-flash",
                1, maxAttempts, forceRefresh, sessionId.ToString(), now,
                Transport: transport));
        }

        if (messages.Count == 0) return 0;

        // Reload so newly inserted jobs are included when recounting totals.
        session = await store.GetSessionAsync(sessionId, ct) ?? session;
        session.TotalPoints = session.PointJobs.Count;
        session.Status = "queued";
        session.UpdatedAt = now;
        session.CompletedAt = null;
        await store.SaveSessionAsync(session, ct);

        await EnqueueJobsAsync(messages, ct);
        await store.UpdateSessionCountsAsync(sessionId, ct);
        logger.LogInformation(
            "Requeued {Count} point(s) on session {SessionId} ({Added} newly added)",
            messages.Count, sessionId, added);
        return messages.Count;
    }

    private async Task AssertPersistenceReadyAsync(CancellationToken ct)
    {
        var health = await GetHealthAsync(ct);
        if (health.Persistence.Mode == "memory")
            throw new InvalidOperationException(
                "Cannot run — Supabase/PostgreSQL not ready. Set ConnectionStrings:PostgreSQL in appsettings.Development.json.");
    }
}
