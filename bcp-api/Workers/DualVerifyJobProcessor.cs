using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.Entities;
using Reguliq.Api.Infrastructure;
using Reguliq.Api.Models;
using Reguliq.Api.Services;

namespace Reguliq.Api.Workers;

/// <summary>Phase 1 → Phase 2 → agreement → persistence for one dual-verify point.</summary>
public class DualVerifyJobProcessor(
    IServiceScopeFactory scopeFactory,
    KafkaConfig kafkaConfig,
    KafkaProducerService kafka,
    LocalJobQueue localQueue,
    IConfiguration config,
    ILogger<DualVerifyJobProcessor> logger)
{
  public async Task ProcessJobAsync(DualVerifyJobMessage job, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<DualVerifyStoreService>();
        var nodeBridge = scope.ServiceProvider.GetRequiredService<NodeBridgeService>();
        var gemini = scope.ServiceProvider.GetRequiredService<GeminiService>();
        var govPoints = scope.ServiceProvider.GetRequiredService<GovPointsService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var existing = await store.GetPointJobAsync(job.SessionId, job.PointId, ct);
        if (existing?.Status is "completed" or "running") return;

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

        var point = new GovPoint(job.PointId, job.PointTitle, job.GovText, null);

        try
        {
            var phase1 = await nodeBridge.ComparePointAsync(
                point, job.InternalFileHash, job.InternalFileName,
                ResolveInternalPdf(store, job), job.ForceRefresh, ct);
            if (string.IsNullOrWhiteSpace(phase1))
                throw new InvalidOperationException("Landing AI returned empty Phase 1 message");

            var markdown = await TryLoadInternalMarkdownAsync(nodeBridge, job, ct);
            var prompt = DualVerifyPromptBuilder.Build(point, phase1, markdown);
            var pdf = ResolveInternalPdf(store, job);
            if ((pdf == null || pdf.Length == 0) && string.IsNullOrWhiteSpace(markdown))
                throw new InvalidOperationException(
                    "Phase 2 needs internal PDF (upload in UI) or parsed markdown. Upload PDF or configure DUAL_VERIFY_INTERNAL_PDF_PATH.");

            var phase2 = pdf is { Length: > 0 }
                ? await gemini.AnalyzeWithPdfAsync(pdf, job.InternalFileName, prompt, job.Phase2Model, ct)
                : await gemini.AnalyzeTextAsync(prompt, job.Phase2Model, ct);

            if (string.IsNullOrWhiteSpace(phase2))
                throw new InvalidOperationException("Gemini returned empty Phase 2 message");

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

            logger.LogInformation("Dual verify completed {Session}:{Point} → {Status}",
                job.SessionId, job.PointId, agreement.Status);
        }
        catch (Exception ex)
        {
            await HandleFailureAsync(store, job, pointJob, ex, ct);
        }
    }

    private byte[]? ResolveInternalPdf(DualVerifyStoreService store, DualVerifyJobMessage job)
    {
        var pdf = store.GetInternalPdf(job.SessionId);
        if (pdf is { Length: > 0 }) return pdf;

        var envPath = config["DUAL_VERIFY_INTERNAL_PDF_PATH"];
        if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
            return File.ReadAllBytes(envPath);

        foreach (var candidate in DefaultPdfCandidates())
        {
            if (File.Exists(candidate))
                return File.ReadAllBytes(candidate);
        }

        return null;
    }

    private static IEnumerable<string> DefaultPdfCandidates()
    {
        var cwd = Directory.GetCurrentDirectory();
        yield return Path.Combine(cwd, "apps", "web", "public", "default-docs", "imptfs.pdf");
        yield return Path.GetFullPath(Path.Combine(cwd, "..", "..", "..", "..", "web", "public", "default-docs", "imptfs.pdf"));
        yield return Path.GetFullPath(Path.Combine(cwd, "..", "..", "..", "..", "..", "web", "public", "default-docs", "imptfs.pdf"));
    }

    private static async Task<string?> TryLoadInternalMarkdownAsync(
        NodeBridgeService nodeBridge,
        DualVerifyJobMessage job,
        CancellationToken ct)
    {
        try
        {
            var md = await nodeBridge.GetStoredParseAsync(job.InternalFileHash, ct);
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
        return m.Contains("timeout") || m.Contains("429") || m.Contains("503") || m.Contains("502")
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
