using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Reguliq.Api.Data.Entities;
using Reguliq.Api.Models;
using Reguliq.Api.Workers;

namespace Reguliq.Api.Services;

public class DualVerifyService(
    GovPointsService govPoints,
    DualVerifyStoreService store,
    LocalJobQueue queue,
    IConfiguration config,
    ILogger<DualVerifyService> logger)
{
    private const string GovFileHash = "c84713f9aacd18415680356aeae47bcacff9c17458b5595b575400b12fe8f2ff";
    private const string InternalFileHash = "6a0a0bd13c7a32ea10c43c9a8391347a7e0caceaa0b17dd6443e9ee622111717";

    public async Task<DualVerifyHealthDto> GetHealthAsync(CancellationToken ct = default)
    {
        var tablesReady = await store.TablesReadyAsync();
        var kafkaEnabled = config.GetValue("KAFKA_ENABLED", false);
        var mode = tablesReady ? "file" : "memory";
        return new DualVerifyHealthDto(
            "ok",
            kafkaEnabled ? "kafka" : "local",
            kafkaEnabled,
            new KafkaTopicsDto(
                config["KAFKA_TOPIC_JOBS"] ?? "dual-verify-jobs",
                config["KAFKA_TOPIC_RETRY"] ?? "dual-verify-retry",
                config["KAFKA_TOPIC_DLQ"] ?? "dual-verify-dlq",
                config["KAFKA_TOPIC_RESULTS"] ?? "dual-verify-results"),
            new PersistenceDto(
                tablesReady, tablesReady, Directory.Exists(store.DataDir),
                store.DataDir, mode,
                mode == "memory" ? "Database not connected — SQLite file at data/reguliq.db" : null));
    }

    public async Task<DualVerifySession> CreateJobAsync(
        CreateDualVerifyJobRequest request,
        byte[]? internalPdf,
        string? internalFileName,
        CancellationToken ct = default)
    {
        await AssertPersistenceReadyAsync(ct);

        var filtered = govPoints.FilterByGranularity(request.Granularity)
            .Where(p => request.PointIds.Contains(p.PointId))
            .ToList();
        if (filtered.Count == 0)
            throw new InvalidOperationException("No matching gov points selected");

        var sessionId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var transport = config.GetValue("KAFKA_ENABLED", false) ? "kafka" : "local";

        var session = new DualVerifySession
        {
            Id = sessionId,
            Status = "queued",
            Granularity = request.Granularity,
            GovDocId = request.GovDocId,
            InternalDocId = request.InternalDocId,
            GovFileHash = GovFileHash,
            InternalFileHash = InternalFileHash,
            GovFileName = "TFS Guidelines.pdf",
            InternalFileName = internalFileName ?? "I M P T F S.pdf",
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
                MaxAttempts = config.GetValue("DUAL_VERIFY_MAX_RETRIES", 3),
                CreatedAt = now,
                UpdatedAt = now
            };
            await store.SavePointJobAsync(pointJob, ct);

            queue.Enqueue(new DualVerifyJobMessage(
                Guid.NewGuid().ToString(), jobId, sessionId,
                point.PointId, point.Title, point.Text,
                request.Granularity, request.GovDocId, request.InternalDocId,
                GovFileHash, InternalFileHash,
                session.GovFileName ?? "", session.InternalFileName ?? "",
                request.Phase2Model, 1, pointJob.MaxAttempts,
                request.ForceRefresh, sessionId.ToString(), now));
        }

        logger.LogInformation("Created dual verify session {SessionId} with {Count} points", sessionId, filtered.Count);
        return session;
    }

    public async Task<SessionProgressDto?> GetProgressAsync(Guid sessionId, CancellationToken ct = default)
    {
        var session = await store.GetSessionAsync(sessionId, ct);
        if (session == null) return null;
        return MapProgress(session);
    }

    public static SessionProgressDto MapProgress(DualVerifySession session) =>
        new(
            new DualVerifySessionDto(
                session.Id, session.Status, session.TotalPoints,
                session.CompletedPoints, session.FailedPoints,
                session.RunningPoints, session.QueuedPoints,
                session.Transport, session.Phase2Model ?? "",
                session.Granularity, session.UpdatedAt),
            session.PointJobs.OrderBy(p => p.PointId).Select(p => new PointJobDto(
                p.Id, p.PointId, p.PointTitle, p.Status,
                p.LandingMessage, p.LlmMessage,
                DualVerifyAgreementService.FromJson(p.AgreementJson),
                p.ErrorMessage)).ToList());

    public async Task<int> RetryFailedAsync(Guid sessionId, CancellationToken ct = default)
    {
        var session = await store.GetSessionAsync(sessionId, ct);
        if (session == null) return 0;
        var failed = session.PointJobs.Where(p => p.Status == "failed").ToList();
        foreach (var job in failed)
        {
            job.Status = "queued";
            job.Attempt = 1;
            job.ErrorMessage = null;
            job.UpdatedAt = DateTime.UtcNow;
            await store.SavePointJobAsync(job, ct);
            queue.Enqueue(new DualVerifyJobMessage(
                Guid.NewGuid().ToString(), job.Id, sessionId,
                job.PointId, job.PointTitle, job.GovText,
                session.Granularity, session.GovDocId, session.InternalDocId,
                session.GovFileHash, session.InternalFileHash,
                session.GovFileName ?? "", session.InternalFileName ?? "",
                session.Phase2Model ?? "gemini-2.5-flash-lite",
                1, job.MaxAttempts, false, sessionId.ToString(), DateTime.UtcNow));
        }
        await store.UpdateSessionCountsAsync(sessionId, ct);
        return failed.Count;
    }

    private async Task AssertPersistenceReadyAsync(CancellationToken ct)
    {
        var health = await GetHealthAsync(ct);
        if (health.Persistence.Mode == "memory")
            throw new InvalidOperationException("Cannot run — persistence not ready. Configure PostgreSQL connection string.");
    }
}
