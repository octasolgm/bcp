using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.Entities;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Models;
using Reguliq.Api.Services;

namespace Reguliq.Api.Services.NewDashboard;

/// <summary>
/// Builds completed ND analysis runs from seeded compliance session data (no AI calls).
/// </summary>
public class DemoAnalysisSeedService(AppDbContext db)
{
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
                     && r.Name.StartsWith("[Demo]", StringComparison.Ordinal),
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
}
