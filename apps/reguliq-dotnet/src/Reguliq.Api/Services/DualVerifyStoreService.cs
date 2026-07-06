using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.Entities;
using Reguliq.Api.Models;
using Reguliq.Api.Services;

namespace Reguliq.Api.Services;

public class DualVerifyStoreService(
    AppDbContext db,
    SessionPdfCache pdfCache,
    IWebHostEnvironment env,
    IConfiguration config,
    ILogger<DualVerifyStoreService> logger)
{
    private readonly string _dataDir = ResolveDataDir(env, config);

    public string DataDir => _dataDir;

    public void SetInternalPdf(Guid sessionId, byte[] pdf) => pdfCache.Set(sessionId, pdf);

    public byte[]? GetInternalPdf(Guid sessionId) => pdfCache.Get(sessionId);

    public async Task<bool> TablesReadyAsync()
    {
        try
        {
            return await db.Database.CanConnectAsync();
        }
        catch
        {
            return false;
        }
    }

    public async Task SaveSessionAsync(DualVerifySession session, CancellationToken ct = default)
    {
        var existing = await db.DualVerifySessions.FindAsync([session.Id], ct);
        if (existing == null)
            db.DualVerifySessions.Add(session);
        else
            db.Entry(existing).CurrentValues.SetValues(session);
        await db.SaveChangesAsync(ct);
        await PersistDiskAsync(session.Id, ct);
    }

    public async Task SavePointJobAsync(DualVerifyPointJob job, CancellationToken ct = default)
    {
        var existing = await db.DualVerifyPointJobs.FindAsync([job.Id], ct);
        if (existing == null)
            db.DualVerifyPointJobs.Add(job);
        else
            db.Entry(existing).CurrentValues.SetValues(job);
        await db.SaveChangesAsync(ct);
        await PersistDiskAsync(job.SessionId, ct);
    }

    public async Task<DualVerifySession?> GetSessionAsync(Guid id, CancellationToken ct = default) =>
        await db.DualVerifySessions.Include(s => s.PointJobs).FirstOrDefaultAsync(s => s.Id == id, ct);

    public async Task<DualVerifyPointJob?> GetPointJobAsync(Guid sessionId, string pointId, CancellationToken ct = default) =>
        await db.DualVerifyPointJobs.FirstOrDefaultAsync(p => p.SessionId == sessionId && p.PointId == pointId, ct);

    public async Task<List<DualVerifySession>> ListRecentAsync(int limit = 30, CancellationToken ct = default) =>
        await db.DualVerifySessions.OrderByDescending(s => s.UpdatedAt).Take(limit).ToListAsync(ct);

    public async Task UpdateSessionCountsAsync(Guid sessionId, CancellationToken ct = default)
    {
        var session = await GetSessionAsync(sessionId, ct);
        if (session == null) return;
        var points = session.PointJobs;
        session.CompletedPoints = points.Count(p => p.Status == "completed");
        session.FailedPoints = points.Count(p => p.Status == "failed");
        session.RunningPoints = points.Count(p => p.Status == "running");
        session.QueuedPoints = points.Count(p => p.Status == "queued");
        session.UpdatedAt = DateTime.UtcNow;
        if (session.CompletedPoints + session.FailedPoints >= session.TotalPoints && session.TotalPoints > 0)
        {
            session.Status = session.FailedPoints > 0 && session.CompletedPoints == 0 ? "failed" : "completed";
            session.CompletedAt ??= DateTime.UtcNow;
        }
        else if (session.RunningPoints > 0 || session.CompletedPoints > 0)
            session.Status = "processing";
        await SaveSessionAsync(session, ct);
    }

    private async Task PersistDiskAsync(Guid sessionId, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(_dataDir);
            var session = await GetSessionAsync(sessionId, ct);
            if (session == null) return;
            var path = Path.Combine(_dataDir, $"{sessionId}.json");
            var json = JsonSerializer.Serialize(new { session, points = session.PointJobs });
            await File.WriteAllTextAsync(path, json, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Disk persist failed for {SessionId}", sessionId);
        }
    }

    private static string ResolveDataDir(IWebHostEnvironment env, IConfiguration config)
    {
        var configured = config["DUAL_VERIFY_DATA_DIR"];
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        return Path.Combine(env.ContentRootPath, "data", "dual-verify-kafka");
    }
}
