using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.Entities;
using Reguliq.Api.Infrastructure;
using Reguliq.Api.Models;
using Reguliq.Api.Services;
using Reguliq.Api.Services.LandingAi;

namespace Reguliq.Api.Workers;

/// <summary>Phase 1 → Phase 2 → agreement → persistence for one dual-verify point.</summary>
public class DualVerifyJobProcessor(
    IServiceScopeFactory scopeFactory,
    KafkaConfig kafkaConfig,
    KafkaProducerService kafka,
    LocalJobQueue localQueue,
    DualVerifyJobStageTracker stageTracker,
    SessionCancellationTracker cancellationTracker,
    ILogger<DualVerifyJobProcessor> logger)
{
  public async Task ProcessJobAsync(DualVerifyJobMessage job, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<DualVerifyStoreService>();
        var landingAi = scope.ServiceProvider.GetRequiredService<LandingAiCompareService>();
        var dualVerifyLlm = scope.ServiceProvider.GetRequiredService<DualVerifyLlmService>();
        var govPoints = scope.ServiceProvider.GetRequiredService<GovPointsService>();
        var compliancePdf = scope.ServiceProvider.GetRequiredService<CompliancePdfResolver>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (await ShouldCancelAsync(store, job, ct))
            return;

        var existing = await store.GetPointJobAsync(job.SessionId, job.PointId, ct);
        if (existing?.Status is "completed" or "running" or "cancelled") return;

        var now = DateTime.UtcNow;
        var pointJob = existing ?? new DualVerifyPointJob
        {
            Id = job.JobId,
            SessionId = job.SessionId,
            PointId = job.PointId,
            PointTitle = job.PointTitle,
            GovText = job.GovText,
            CreatedAt = now
        };

        pointJob.Status = "running";
        pointJob.Attempt = job.Attempt;
        pointJob.MaxAttempts = job.MaxAttempts;
        pointJob.StartedAt = now;
        pointJob.UpdatedAt = now;
        await store.SavePointJobAsync(pointJob, ct);
        await store.UpdateSessionCountsAsync(job.SessionId, ct);

        var sw = Stopwatch.StartNew();
        var point = new GovPoint(job.PointId, job.PointTitle, job.GovText, null);

        try
        {
            var (phase1, pass1Cached) = await GetOrRunPass1Async(
                store, landingAi, compliancePdf, job, pointJob, point, ct);
            if (string.IsNullOrWhiteSpace(phase1))
                throw new InvalidOperationException("Landing AI returned empty Phase 1 message");

            if (await ShouldCancelAsync(store, job, ct))
                return;

            var phase1Sec = pass1Cached ? 0 : sw.Elapsed.TotalSeconds;
            var markdown = await TryLoadInternalMarkdownAsync(landingAi, job, ct);
            var prompt = DualVerifyPromptBuilder.Build(point, phase1, markdown);
            var pdf = await compliancePdf.ResolveForWorkerAsync(job, ct);
            if ((pdf == null || pdf.Length == 0) && string.IsNullOrWhiteSpace(markdown))
                throw new InvalidOperationException(
                    "Phase 2 needs internal PDF (upload in UI) or parsed markdown. Upload PDF or configure DUAL_VERIFY_INTERNAL_PDF_PATH.");

            string phase2;
            if (!string.IsNullOrWhiteSpace(markdown) && markdown.Length > 100)
            {
                SetStage(job, pass1Cached ? "Pass 2 — LLM (resume, text)…" : "Pass 2 — LLM (text)…");
                logger.LogInformation(
                    "Dual verify {Session}:{Point} — Pass 2 text mode{Resume}",
                    job.SessionId, job.PointId, pass1Cached ? ", Pass 1 cached" : "");
                phase2 = await dualVerifyLlm.AnalyzeTextAsync(prompt, ct);
            }
            else
            {
                SetStage(job, pass1Cached ? "Pass 2 — LLM (resume, PDF)…" : "Pass 2 — LLM (PDF)…");
                logger.LogInformation(
                    "Dual verify {Session}:{Point} — Pass 2 PDF mode ({Kb} KB{Resume})",
                    job.SessionId, job.PointId, (pdf?.Length ?? 0) / 1024,
                    pass1Cached ? ", Pass 1 cached" : "");
                phase2 = await dualVerifyLlm.AnalyzeWithPdfsAsync(
                    [(pdf!, job.InternalFileName)], prompt, ct);
            }

            if (string.IsNullOrWhiteSpace(phase2))
                throw new InvalidOperationException("Dual verify LLM returned empty Phase 2 message");
            logger.LogInformation(
                "Dual verify {Session}:{Point} — Pass 2 done in {Sec:F1}s (total {Total:F1}s)",
                job.SessionId, job.PointId, sw.Elapsed.TotalSeconds - phase1Sec, sw.Elapsed.TotalSeconds);

            SetStage(job, "Saving results…");
            var agreement = DualVerifyAgreementService.Compare(phase1, phase2);

            pointJob.Status = "completed";
            pointJob.LandingMessage = phase1;
            pointJob.LlmMessage = phase2;
            pointJob.AgreementJson = DualVerifyAgreementService.ToJson(agreement);
            pointJob.CompletedAt = DateTime.UtcNow;
            pointJob.UpdatedAt = DateTime.UtcNow;
            await store.SavePointJobAsync(pointJob, ct);
            await SaveComplianceIncrementalAsync(
                db, govPoints, job, point, phase1, phase2, agreement,
                kafkaConfig.GetTransportMode(), ct);
            await store.UpdateSessionCountsAsync(job.SessionId, ct);

            if (kafkaConfig.IsEnabled())
            {
                await kafka.PublishToTopicAsync("results", new
                {
                    sessionId = job.SessionId,
                    pointId = job.PointId,
                    status = "completed",
                    agreement = agreement.Status,
                    completedAt = DateTime.UtcNow
                }, ct);
            }

            logger.LogInformation("Dual verify completed {Session}:{Point} → {Status} in {Sec:F1}s",
                job.SessionId, job.PointId, agreement.Status, sw.Elapsed.TotalSeconds);
            stageTracker.Clear(job.SessionId, job.PointId);
        }
        catch (Exception ex)
        {
            stageTracker.Clear(job.SessionId, job.PointId);
            await HandleFailureAsync(store, job, pointJob, ex, ct);
        }
    }

    private void SetStage(DualVerifyJobMessage job, string stage) =>
        stageTracker.Set(job.SessionId, job.PointId, stage);

    /// <summary>Run Pass 1 or reuse cached Landing AI output saved on a prior attempt.</summary>
    private async Task<(string Phase1, bool Cached)> GetOrRunPass1Async(
        DualVerifyStoreService store,
        LandingAiCompareService landingAi,
        CompliancePdfResolver compliancePdf,
        DualVerifyJobMessage job,
        DualVerifyPointJob pointJob,
        GovPoint point,
        CancellationToken ct)
    {
        if (!job.ForceRefresh && !string.IsNullOrWhiteSpace(pointJob.LandingMessage))
        {
            logger.LogInformation(
                "Dual verify {Session}:{Point} — Pass 1 skipped (cached Landing AI result)",
                job.SessionId, job.PointId);
            return (pointJob.LandingMessage, true);
        }

        SetStage(job, "Pass 1 — Landing AI compare…");
        logger.LogInformation("Dual verify {Session}:{Point} — Pass 1 starting", job.SessionId, job.PointId);
        var phase1 = await landingAi.ComparePointAsync(
            point, job.InternalFileHash, job.InternalFileName,
            await compliancePdf.ResolveForWorkerAsync(job, ct), job.ForceRefresh, ct);
        logger.LogInformation(
            "Dual verify {Session}:{Point} — Pass 1 done",
            job.SessionId, job.PointId);

        pointJob.LandingMessage = phase1;
        pointJob.UpdatedAt = DateTime.UtcNow;
        await store.SavePointJobAsync(pointJob, ct);
        return (phase1, false);
    }

    private async Task<bool> ShouldCancelAsync(
        DualVerifyStoreService store,
        DualVerifyJobMessage job,
        CancellationToken ct)
    {
        if (cancellationTracker.IsCancelled(job.SessionId))
        {
            await MarkPointCancelledAsync(store, job, ct);
            return true;
        }

        var session = await store.GetSessionAsync(job.SessionId, ct);
        if (session?.Status == "cancelled")
        {
            cancellationTracker.MarkCancelled(job.SessionId);
            await MarkPointCancelledAsync(store, job, ct);
            return true;
        }

        return false;
    }

    private static async Task MarkPointCancelledAsync(
        DualVerifyStoreService store,
        DualVerifyJobMessage job,
        CancellationToken ct)
    {
        var pointJob = await store.GetPointJobAsync(job.SessionId, job.PointId, ct);
        if (pointJob == null || pointJob.Status is "completed" or "cancelled") return;

        pointJob.Status = "cancelled";
        pointJob.ErrorMessage = "Cancelled by user";
        pointJob.UpdatedAt = DateTime.UtcNow;
        await store.SavePointJobAsync(pointJob, ct);
        await store.UpdateSessionCountsAsync(job.SessionId, ct);
    }

    private static async Task<string?> TryLoadInternalMarkdownAsync(
        LandingAiCompareService landingAi,
        DualVerifyJobMessage job,
        CancellationToken ct)
    {
        try
        {
            var md = await landingAi.GetStoredParseAsync(job.InternalFileHash, ct);
            return md is { Length: > 100 } ? md : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task HandleFailureAsync(
        DualVerifyStoreService store,
        DualVerifyJobMessage job,
        DualVerifyPointJob pointJob,
        Exception ex,
        CancellationToken ct)
    {
        var message = ex.Message;
        var isTransient = IsTransientError(message);
        var canRetry = isTransient && job.Attempt < job.MaxAttempts;

        if (canRetry)
        {
            var retryJob = job with
            {
                MessageId = Guid.NewGuid().ToString(),
                Attempt = job.Attempt + 1,
                CreatedAt = DateTime.UtcNow
            };

            logger.LogWarning("Retry {Point} attempt {Attempt}/{Max}: {Message}",
                job.PointId, retryJob.Attempt, job.MaxAttempts, message);

            pointJob.Status = "queued";
            pointJob.Attempt = retryJob.Attempt;
            pointJob.ErrorMessage = message;
            pointJob.UpdatedAt = DateTime.UtcNow;
            // LandingMessage kept — retry resumes at Pass 2 (Gemini).
            await store.SavePointJobAsync(pointJob, ct);

            var delayMs = kafkaConfig.IsEnabled() ? 5000 * retryJob.Attempt : 3000 * retryJob.Attempt;
            _ = Task.Run(async () =>
            {
                await Task.Delay(delayMs, ct);
                if (kafkaConfig.IsEnabled())
                {
                    try
                    {
                        await kafka.PublishToTopicAsync("retry", retryJob, ct);
                        await kafka.PublishJobAsync(retryJob, ct);
                    }
                    catch (Exception e)
                    {
                        logger.LogError(e, "Retry publish failed for {Point}", job.PointId);
                    }
                }
                else
                {
                    localQueue.Enqueue(retryJob);
                }
            }, ct);
            return;
        }

        logger.LogError(ex, "Point {PointId} failed permanently", job.PointId);
        pointJob.Status = "failed";
        pointJob.ErrorMessage = message;
        pointJob.CompletedAt = DateTime.UtcNow;
        pointJob.UpdatedAt = DateTime.UtcNow;
        // Keep LandingMessage when Pass 1 succeeded — UI can show partial results on retry.
        await store.SavePointJobAsync(pointJob, ct);
        await store.UpdateSessionCountsAsync(job.SessionId, ct);

        if (kafkaConfig.IsEnabled())
        {
            await kafka.PublishToTopicAsync("dlq", new { job, errorMessage = message }, ct);
        }
    }

    private static bool IsTransientError(string message)
    {
        var m = message.ToLowerInvariant();
        return m.Contains("timeout") || m.Contains("elapsing") || m.Contains("canceled")
            || m.Contains("429") || m.Contains("503") || m.Contains("502")
            || m.Contains("serviceunavailable") || m.Contains("too many requests")
            || m.Contains("quota") || m.Contains("rate limit") || m.Contains("econnreset")
            || m.Contains("fetch failed") || m.Contains("network") || m.Contains("socket");
    }

    private static async Task SaveComplianceIncrementalAsync(
        AppDbContext db,
        GovPointsService govPoints,
        DualVerifyJobMessage job,
        GovPoint point,
        string phase1,
        string phase2,
        DualVerifyAgreementDto agreement,
        string transport,
        CancellationToken ct)
    {
        var granularity = job.Granularity == "leaf" ? "dual-leaf" : "dual-section";
        var sessionKey = ComputeSessionKey(job.GovFileHash, job.InternalFileHash, granularity);

        var existing = await db.ComplianceSessions.FirstOrDefaultAsync(s => s.SessionKey == sessionKey, ct);
        var resultEntry = new
        {
            point_id = point.PointId,
            title = point.Title,
            text = point.Text,
            message = phase1,
            landingMessage = phase1,
            llmMessage = phase2,
            agreementJson = agreement
        };

        if (existing == null)
        {
            existing = new ComplianceSession
            {
                Id = Guid.NewGuid(),
                SessionKey = sessionKey,
                GovFileHash = job.GovFileHash,
                InternalFileHash = job.InternalFileHash,
                GovFileName = job.GovFileName,
                InternalFileName = job.InternalFileName,
                TotalGovPoints = govPoints.GetAllPoints().Count,
                ComparedPoints = 1,
                ResultsJson = JsonSerializer.Serialize(new[] { resultEntry }),
                SummaryJson = JsonSerializer.Serialize(new
                {
                    pipeline = "kafka-dual-verify",
                    granularity,
                    sessionId = job.SessionId,
                    phase2Model = job.Phase2Model,
                    transport
                }),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.ComplianceSessions.Add(existing);
        }
        else
        {
            var results = JsonSerializer.Deserialize<List<JsonElement>>(existing.ResultsJson) ?? [];
            var list = results.ToList();
            list.RemoveAll(r => r.TryGetProperty("point_id", out var id) && id.GetString() == point.PointId);
            list.Add(JsonSerializer.SerializeToElement(resultEntry));
            existing.ResultsJson = JsonSerializer.Serialize(list);
            existing.ComparedPoints = list.Count;
            existing.UpdatedAt = DateTime.UtcNow;
            db.ComplianceSessions.Update(existing);
        }
        await db.SaveChangesAsync(ct);
    }

    private static string ComputeSessionKey(string govHash, string internalHash, string granularity)
    {
        var input = $"{govHash}:{internalHash}:{granularity}";
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
