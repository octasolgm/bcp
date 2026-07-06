using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.Entities;
using Reguliq.Api.Models;
using Reguliq.Api.Services;

namespace Reguliq.Api.Workers;

public class DualVerifyWorker(
    IServiceScopeFactory scopeFactory,
    IConfiguration config,
    ILogger<DualVerifyWorker> logger)
{
    public void Register(LocalJobQueue queue)
    {
        queue.SetConcurrency(config.GetValue("DUAL_VERIFY_WORKER_CONCURRENCY", 2));
        queue.RegisterHandler(ProcessJobAsync);
        _ = queue.StartAsync(CancellationToken.None);
        logger.LogInformation("Dual verify worker registered (local queue)");
    }

    private async Task ProcessJobAsync(DualVerifyJobMessage job, CancellationToken ct)
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
        pointJob.StartedAt = now;
        pointJob.UpdatedAt = now;
        await store.SavePointJobAsync(pointJob, ct);
        await store.UpdateSessionCountsAsync(job.SessionId, ct);

        var point = new GovPoint(job.PointId, job.PointTitle, job.GovText, null);

        try
        {
            var pdf = store.GetInternalPdf(job.SessionId);
            if (pdf == null || pdf.Length == 0)
                throw new InvalidOperationException("Internal PDF not uploaded for this session");

            var phase1 = await nodeBridge.ComparePointAsync(
                point, job.InternalFileHash, job.InternalFileName, pdf, job.ForceRefresh, ct);

            var prompt = DualVerifyPromptBuilder.Build(point, phase1);
            var phase2 = await gemini.AnalyzeWithPdfAsync(pdf, job.InternalFileName, prompt, job.Phase2Model, ct);
            var agreement = DualVerifyAgreementService.Compare(phase1, phase2);

            pointJob.Status = "completed";
            pointJob.LandingMessage = phase1;
            pointJob.LlmMessage = phase2;
            pointJob.AgreementJson = DualVerifyAgreementService.ToJson(agreement);
            pointJob.CompletedAt = DateTime.UtcNow;
            pointJob.UpdatedAt = DateTime.UtcNow;
            await store.SavePointJobAsync(pointJob, ct);
            await SaveComplianceIncrementalAsync(db, govPoints, job, point, phase1, phase2, agreement, ct);
            await store.UpdateSessionCountsAsync(job.SessionId, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Point {PointId} failed", job.PointId);
            pointJob.Status = "failed";
            pointJob.ErrorMessage = ex.Message;
            pointJob.CompletedAt = DateTime.UtcNow;
            pointJob.UpdatedAt = DateTime.UtcNow;
            await store.SavePointJobAsync(pointJob, ct);
            await store.UpdateSessionCountsAsync(job.SessionId, ct);
        }
    }

    private static async Task SaveComplianceIncrementalAsync(
        AppDbContext db,
        GovPointsService govPoints,
        DualVerifyJobMessage job,
        GovPoint point,
        string phase1,
        string phase2,
        DualVerifyAgreementDto agreement,
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
                SummaryJson = JsonSerializer.Serialize(new { pipeline = "kafka-dual-verify", granularity }),
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

public class DualVerifyWorkerHosted(DualVerifyWorker worker, LocalJobQueue queue) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        worker.Register(queue);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
