using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.Entities;
using Reguliq.Api.Infrastructure;

namespace Reguliq.Api.Services;

/// <summary>One-time import of local SQLite + JSON session files into Supabase.</summary>
public class LocalDataMigrationService(
    AppDbContext db,
    DatabaseConfig dbConfig,
    IWebHostEnvironment env,
    IConfiguration config,
    ILogger<LocalDataMigrationService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
    };

    public async Task MigrateAsync(CancellationToken ct = default)
    {
        if (!dbConfig.UsePostgres)
            return;

        var flag = BcpConfiguration.GetString(
            config,
            "Bcp:MigrateLocalDataToSupabase",
            "MIGRATE_LOCAL_DATA_TO_SUPABASE")
            ?? "true";
        if (flag is "false" or "0")
        {
            logger.LogInformation("Local → Supabase migration skipped (MIGRATE_LOCAL_DATA_TO_SUPABASE=false)");
            return;
        }

        var imported = 0;
        imported += await ImportJsonSessionsAsync(ct);
        imported += await ImportSqliteAsync(ct);

        if (imported > 0)
            logger.LogInformation("Imported {Count} local record(s) into Supabase", imported);
        else
            logger.LogInformation("No local session files to import into Supabase");
    }

    private async Task<int> ImportJsonSessionsAsync(CancellationToken ct)
    {
        var dir = Path.Combine(env.ContentRootPath, "data", "dual-verify-kafka");
        if (!Directory.Exists(dir))
            return 0;

        var count = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                var json = await File.ReadAllTextAsync(file, ct);
                var bundle = JsonSerializer.Deserialize<DiskSessionBundle>(json, JsonOptions);
                if (bundle?.Session == null)
                    continue;

                NormalizeTimestamps(bundle.Session);
                foreach (var point in bundle.Points ?? [])
                    NormalizeTimestamps(point);

                if (await UpsertSessionAsync(bundle.Session, ct))
                    count++;

                foreach (var point in bundle.Points ?? [])
                {
                    point.SessionId = bundle.Session.Id;
                    point.Session = null!;
                    if (await UpsertPointJobAsync(point, ct))
                        count++;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Skipped local JSON import for {File}", file);
            }
        }

        return count;
    }

    private async Task<int> ImportSqliteAsync(CancellationToken ct)
    {
        if (!File.Exists(dbConfig.SqlitePath))
            return 0;

        var count = 0;
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={dbConfig.SqlitePath}")
                .Options;

            await using var sqlite = new AppDbContext(options);

            var sessions = await sqlite.DualVerifySessions
                .AsNoTracking()
                .Include(s => s.PointJobs)
                .ToListAsync(ct);

            foreach (var session in sessions)
            {
                var points = session.PointJobs.ToList();
                session.PointJobs = new List<DualVerifyPointJob>();
                NormalizeTimestamps(session);

                if (await UpsertSessionAsync(session, ct))
                    count++;

                foreach (var point in points)
                {
                    point.SessionId = session.Id;
                    point.Session = null!;
                    NormalizeTimestamps(point);
                    if (await UpsertPointJobAsync(point, ct))
                        count++;
                }
            }

            var compliance = await sqlite.ComplianceSessions.AsNoTracking().ToListAsync(ct);
            foreach (var row in compliance)
            {
                NormalizeTimestamps(row);
                if (await UpsertComplianceSessionAsync(row, ct))
                    count++;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SQLite local import failed from {Path}", dbConfig.SqlitePath);
        }

        return count;
    }

    private async Task<bool> UpsertSessionAsync(DualVerifySession session, CancellationToken ct)
    {
        var existing = await db.DualVerifySessions.FindAsync([session.Id], ct);
        if (existing == null)
        {
            db.DualVerifySessions.Add(session);
            await db.SaveChangesAsync(ct);
            return true;
        }

        if (existing.UpdatedAt >= session.UpdatedAt)
            return false;

        db.Entry(existing).CurrentValues.SetValues(session);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<bool> UpsertPointJobAsync(DualVerifyPointJob job, CancellationToken ct)
    {
        var existing = await db.DualVerifyPointJobs.FindAsync([job.Id], ct);
        if (existing == null)
        {
            db.DualVerifyPointJobs.Add(job);
            await db.SaveChangesAsync(ct);
            return true;
        }

        if (existing.UpdatedAt >= job.UpdatedAt)
            return false;

        db.Entry(existing).CurrentValues.SetValues(job);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<bool> UpsertComplianceSessionAsync(ComplianceSession row, CancellationToken ct)
    {
        var existing = await db.ComplianceSessions
            .FirstOrDefaultAsync(s => s.Id == row.Id || s.SessionKey == row.SessionKey, ct);

        if (existing == null)
        {
            db.ComplianceSessions.Add(row);
            await db.SaveChangesAsync(ct);
            return true;
        }

        if (existing.UpdatedAt >= row.UpdatedAt)
            return false;

        existing.SessionKey = row.SessionKey;
        existing.GovFileHash = row.GovFileHash;
        existing.InternalFileHash = row.InternalFileHash;
        existing.GovFileName = row.GovFileName;
        existing.InternalFileName = row.InternalFileName;
        existing.TotalGovPoints = row.TotalGovPoints;
        existing.ComparedPoints = row.ComparedPoints;
        existing.SkippedPoints = row.SkippedPoints;
        existing.SkippedJson = row.SkippedJson;
        existing.ResultsJson = row.ResultsJson;
        existing.SummaryJson = row.SummaryJson;
        existing.UpdatedAt = row.UpdatedAt;
        await db.SaveChangesAsync(ct);
        return true;
    }

    private sealed class DiskSessionBundle
    {
        public DualVerifySession? Session { get; set; }
        public List<DualVerifyPointJob>? Points { get; set; }
    }

    private static void NormalizeTimestamps(DualVerifySession session)
    {
        session.CreatedAt = ToUtc(session.CreatedAt);
        session.UpdatedAt = ToUtc(session.UpdatedAt);
        if (session.CompletedAt.HasValue)
            session.CompletedAt = ToUtc(session.CompletedAt.Value);
    }

    private static void NormalizeTimestamps(DualVerifyPointJob job)
    {
        job.CreatedAt = ToUtc(job.CreatedAt);
        job.UpdatedAt = ToUtc(job.UpdatedAt);
        if (job.StartedAt.HasValue)
            job.StartedAt = ToUtc(job.StartedAt.Value);
        if (job.CompletedAt.HasValue)
            job.CompletedAt = ToUtc(job.CompletedAt.Value);
    }

    private static void NormalizeTimestamps(ComplianceSession row)
    {
        row.CreatedAt = ToUtc(row.CreatedAt);
        row.UpdatedAt = ToUtc(row.UpdatedAt);
    }

    private static DateTime ToUtc(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
}
