using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Infrastructure;
using Reguliq.Api.Infrastructure.NewDashboard;
using Reguliq.Api.Services.NewDashboard;

namespace Reguliq.Api.Controllers;

/// <summary>Local dev only — stop stuck runs and inspect DB connection pressure.</summary>
[ApiController]
[Route("dev")]
public class DevMaintenanceController(
    AppDbContext db,
    DatabaseConfig dbConfig,
    IConfiguration config,
    NdAnalysisRunCancellationTracker runCancellation,
    IWebHostEnvironment env,
    ILogger<DevMaintenanceController> logger) : ControllerBase
{
    private sealed record PgStateCount(string State, int Count);

    private sealed record PgSessionRow(
        int Pid,
        string State,
        string? WaitEvent,
        DateTime? QueryStart,
        string? QueryPreview);

    [HttpPost("stop-stuck-runs")]
    public async Task<IActionResult> StopStuckRuns(CancellationToken ct)
    {
        if (!env.IsDevelopment())
            return NotFound();

        var cancelled = await NdStaleRunRecovery.CancelAllRunningWithoutWorkerAsync(
            db, runCancellation, logger, ct);

        return Ok(new
        {
            success = true,
            message = cancelled > 0
                ? $"Cancelled {cancelled} stuck run(s)."
                : "No stuck runs (nothing in running/processing without a worker).",
            cancelled,
        });
    }

    [HttpGet("status")]
    public async Task<IActionResult> Status(CancellationToken ct)
    {
        if (!env.IsDevelopment())
            return NotFound();

        var running = await db.NdAnalysisRuns.AsNoTracking()
            .Where(r => r.Status == "running" || r.Status == "processing")
            .Select(r => new { r.Id, r.Name, r.Status, r.UpdatedAt })
            .Take(20)
            .ToListAsync(ct);

        return Ok(new
        {
            success = true,
            runningCount = running.Count,
            activeWorkers = runCancellation.ActiveWorkerCount,
            runs = running,
            hint = "GET /dev/db-status for connection pool + pg_stat_activity detail.",
        });
    }

    /// <summary>
    /// Live Supabase/pg connection snapshot — use to tell pool saturation from undisposed DbContext.
    /// </summary>
    [HttpGet("db-status")]
    public async Task<IActionResult> DbStatus(CancellationToken ct)
    {
        if (!env.IsDevelopment())
            return NotFound();

        var maxPool = config.GetValue("Bcp:PostgresMaxPoolSize", 8);
        var dbPort = config.GetValue("Supabase:DbPort", 6543);

        if (!dbConfig.UsePostgres)
        {
            return Ok(new
            {
                success = true,
                persistence = "sqlite",
                message = "PostgreSQL pool diagnostics only apply when Bcp:UsePostgres=true.",
            });
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        List<PgStateCount> byState;
        List<PgSessionRow> sessions;
        try
        {
            byState = await db.Database.SqlQueryRaw<PgStateCount>(
                """
                SELECT COALESCE(state, '(null)') AS "State", COUNT(*)::int AS "Count"
                FROM pg_stat_activity
                WHERE datname = current_database()
                GROUP BY state
                ORDER BY state
                """).ToListAsync(ct);

            sessions = await db.Database.SqlQueryRaw<PgSessionRow>(
                """
                SELECT pid AS "Pid",
                       COALESCE(state, '(null)') AS "State",
                       COALESCE(wait_event_type, '') AS "WaitEvent",
                       query_start AS "QueryStart",
                       LEFT(query, 100) AS "QueryPreview"
                FROM pg_stat_activity
                WHERE datname = current_database()
                  AND pid <> pg_backend_pid()
                ORDER BY
                  CASE state WHEN 'idle in transaction' THEN 0 WHEN 'active' THEN 1 ELSE 2 END,
                  query_start NULLS LAST
                LIMIT 20
                """).ToListAsync(ct);
        }
        catch (Exception ex)
        {
            return StatusCode(503, new
            {
                success = false,
                message = "Could not query pg_stat_activity.",
                error = ex.Message,
                maxPool,
                dbPort,
            });
        }

        sw.Stop();

        var stateMap = byState.ToDictionary(x => x.State, x => x.Count, StringComparer.OrdinalIgnoreCase);
        var total = byState.Sum(x => x.Count);
        var idleInTx = stateMap.GetValueOrDefault("idle in transaction", 0);
        var active = stateMap.GetValueOrDefault("active", 0);
        var idle = stateMap.GetValueOrDefault("idle", 0);

        var runningDb = await db.NdAnalysisRuns.AsNoTracking()
            .CountAsync(r => r.Status == "running" || r.Status == "processing", ct);

        var diagnosis = new List<string>();
        if (idleInTx > 0)
        {
            diagnosis.Add(
                $"{idleInTx} session(s) idle in transaction — open transaction not committed. " +
                "Check for missing SaveChanges/Commit or exception mid-transaction.");
        }

        if (active + idle >= maxPool * 0.85)
        {
            diagnosis.Add(
                $"Pool pressure: ~{active + idle} client session(s) vs max pool {maxPool}. " +
                "Parallel dashboard calls + analysis workers compete for the same Supabase pooler.");
        }

        if (runCancellation.ActiveWorkerCount > 0)
        {
            diagnosis.Add(
                $"{runCancellation.ActiveWorkerCount} in-process analysis worker(s) hold scoped DbContext " +
                "for the whole run (normal — connection busy until run ends, not a dispose bug).");
        }

        if (runningDb > runCancellation.ActiveWorkerCount)
        {
            diagnosis.Add(
                $"{runningDb - runCancellation.ActiveWorkerCount} DB run(s) marked running/processing " +
                "with no in-process worker — call POST /dev/stop-stuck-runs.");
        }

        if (diagnosis.Count == 0)
            diagnosis.Add("No obvious connection leak pattern. Slow responses are likely query latency or pool saturation under load.");

        var longRunning = sessions
            .Where(s => s.QueryStart.HasValue
                && s.State is "active" or "idle in transaction"
                && DateTime.UtcNow - s.QueryStart.Value.ToUniversalTime() > TimeSpan.FromSeconds(30))
            .Select(s => new
            {
                s.Pid,
                s.State,
                ageSeconds = (int)(DateTime.UtcNow - s.QueryStart!.Value.ToUniversalTime()).TotalSeconds,
                s.QueryPreview,
            })
            .ToList();

        return Ok(new
        {
            success = true,
            queriedMs = sw.ElapsedMilliseconds,
            persistence = "supabase",
            dbPort,
            clientMaxPoolSize = maxPool,
            pgSessionsForDatabase = total,
            byState = byState.Select(x => new { state = x.State, count = x.Count }),
            idleInTransaction = idleInTx,
            activeQueries = active,
            idleConnections = idle,
            inProcessAnalysisWorkers = runCancellation.ActiveWorkerCount,
            activeWorkerRunIds = runCancellation.ActiveRunIds,
            dbRunsMarkedRunning = runningDb,
            longRunningSessions = longRunning,
            recentSessions = sessions.Select(s => new
            {
                s.Pid,
                s.State,
                waitEvent = string.IsNullOrEmpty(s.WaitEvent) ? null : s.WaitEvent,
                s.QueryStart,
                s.QueryPreview,
            }),
            diagnosis,
        });
    }
}
