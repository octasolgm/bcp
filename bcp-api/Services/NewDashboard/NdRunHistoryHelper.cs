using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;

namespace Reguliq.Api.Services.NewDashboard;

public static class NdRunHistoryHelper
{
    public static async Task<object> BuildTimelineAsync(AppDbContext db, NdAnalysisRun run, CancellationToken ct)
    {
        var creator = run.CreatedBy.HasValue
            ? await db.NdProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == run.CreatedBy, ct)
            : null;

        var pointIds = run.Points.Select(p => p.Id).ToList();

        var reviews = await db.NdAnalysisReviews.AsNoTracking()
            .Where(r => r.AnalysisRunId == run.Id)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync(ct);

        var statusHistory = await db.NdAnalysisStatusHistories.AsNoTracking()
            .Where(h => h.AnalysisRunId == run.Id)
            .OrderBy(h => h.CreatedAt)
            .ToListAsync(ct);

        var actionReviews = await db.NdActionPlanItemReviews.AsNoTracking()
            .Where(r => pointIds.Contains(r.AnalysisPointId))
            .Select(r => new { r.CreatedAt, r.ActionIndex, r.AnalysisPointId })
            .ToListAsync(ct);

        var attachmentCountsByPoint = await db.NdAnalysisPointAttachments.AsNoTracking()
            .Where(a => pointIds.Contains(a.AnalysisPointId))
            .GroupBy(a => a.AnalysisPointId)
            .Select(g => new { PointId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.PointId, x => x.Count, ct);

        var totalGaps = NdCapGapCounter.CountForPoints(run.Points, attachmentCountsByPoint);

        var profileIds = reviews.Where(r => r.ReviewerId.HasValue).Select(r => r.ReviewerId!.Value)
            .Concat(statusHistory.Where(h => h.ChangedBy.HasValue).Select(h => h.ChangedBy!.Value))
            .Append(run.CreatedBy ?? Guid.Empty)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();

        var profiles = profileIds.Count == 0
            ? new Dictionary<Guid, NdProfile>()
            : await db.NdProfiles.AsNoTracking()
                .Where(p => profileIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, ct);

        string Name(Guid? id) =>
            id.HasValue && profiles.TryGetValue(id.Value, out var p) ? p.FullName ?? "Unknown" : "Unknown";

        string Role(Guid? id) =>
            id.HasValue && profiles.TryGetValue(id.Value, out var p) ? p.Role : "";

        int ReviewsBefore(DateTimeOffset at) =>
            actionReviews.Count(r => r.CreatedAt <= at);

        int ReviewedActionsBefore(DateTimeOffset at) =>
            actionReviews
                .Where(r => r.CreatedAt <= at && r.ActionIndex >= 1)
                .Select(r => (r.AnalysisPointId, r.ActionIndex))
                .Distinct()
                .Count();

        int GapsBefore(DateTimeOffset at)
        {
            // Gap totals are derived from current run points (no point-in-time snapshot in DB).
            _ = at;
            return totalGaps;
        }

        var events = new List<object>();

        events.Add(new
        {
            id = $"created-{run.Id}",
            kind = "created",
            title = "Analysis created",
            actorId = run.CreatedBy,
            actorName = creator?.FullName ?? "Unknown",
            actorRole = creator?.Role ?? "maker",
            timestamp = run.CreatedAt,
            targetRole = "maker",
            gapCount = 0,
            actionReviewCount = 0,
            reviewedActionsAtEvent = 0,
            pointCount = run.TotalPointsCount,
            meta = new[] { $"{run.TotalPointsCount} points" },
        });

        var reviewTimestamps = new HashSet<long>();

        foreach (var review in reviews)
        {
            reviewTimestamps.Add(review.CreatedAt.ToUnixTimeMilliseconds());

            var (title, targetRole) = DescribeReview(review);
            var reviewsBefore = ReviewsBefore(review.CreatedAt);
            var reviewedActionsBefore = ReviewedActionsBefore(review.CreatedAt);
            var gapsAtEvent = GapsBefore(review.CreatedAt);
            var meta = BuildReviewMeta(review, gapsAtEvent, reviewedActionsBefore, reviewsBefore);

            events.Add(new
            {
                id = review.Id,
                kind = "review",
                title,
                actorId = review.ReviewerId,
                actorName = Name(review.ReviewerId),
                actorRole = InferActorRole(review),
                timestamp = review.CreatedAt,
                targetRole,
                reviewAction = review.Action,
                overallComment = review.OverallComment,
                reviewStatus = review.ReviewStatus,
                priority = review.Priority,
                responsibility = review.Responsibility,
                dueDate = review.DueDate,
                gapCount = gapsAtEvent,
                actionReviewCount = reviewsBefore,
                reviewedActionsAtEvent = reviewedActionsBefore,
                meta,
            });
        }

        foreach (var row in statusHistory)
        {
            if (reviewTimestamps.Contains(row.CreatedAt.ToUnixTimeMilliseconds()))
                continue;

            var closeReview = reviews.Any(r =>
                Math.Abs((r.CreatedAt - row.CreatedAt).TotalSeconds) < 3);
            if (closeReview) continue;

            var reviewsBefore = ReviewsBefore(row.CreatedAt);
            var reviewedActionsBefore = ReviewedActionsBefore(row.CreatedAt);
            var gapsAtEvent = GapsBefore(row.CreatedAt);

            events.Add(new
            {
                id = row.Id,
                kind = "status",
                title = DescribeStatusChange(row.FromStatus, row.ToStatus),
                actorId = row.ChangedBy,
                actorName = Name(row.ChangedBy),
                actorRole = Role(row.ChangedBy),
                timestamp = row.CreatedAt,
                targetRole = TargetRoleForStatus(row.ToStatus),
                fromStatus = row.FromStatus,
                toStatus = row.ToStatus,
                overallComment = row.Comment,
                gapCount = gapsAtEvent,
                actionReviewCount = reviewsBefore,
                reviewedActionsAtEvent = reviewedActionsBefore,
                meta = BuildStatusMeta(gapsAtEvent, reviewedActionsBefore, reviewsBefore),
            });
        }

        var ordered = events
            .Select(e => (Event: e, At: GetTimestamp(e)))
            .OrderBy(x => x.At)
            .Select(x => x.Event)
            .ToList();

        return new
        {
            runId = run.Id,
            runName = run.Name,
            currentStatus = run.Status,
            totalGaps,
            totalActionReviews = actionReviews.Count,
            reviewedActions = actionReviews
                .Where(r => r.ActionIndex >= 1)
                .Select(r => (r.AnalysisPointId, r.ActionIndex))
                .Distinct()
                .Count(),
            events = ordered,
        };
    }

    private static DateTimeOffset GetTimestamp(object evt)
    {
        var prop = evt.GetType().GetProperty("timestamp");
        if (prop?.GetValue(evt) is DateTimeOffset dto) return dto;
        return DateTimeOffset.MinValue;
    }

    private static (string Title, string? TargetRole) DescribeReview(NdAnalysisReview review)
    {
        var action = review.Action?.Trim().ToLowerInvariant() ?? "";
        var role = review.ReviewerRole?.Trim().ToLowerInvariant() ?? "";

        return action switch
        {
            "submitted" => ("Submitted for review", "checker"),
            "approved" when role == "checker" => ("Checker approved", "reviewer"),
            "pulled_back" when role == "checker" => ("Pulled back to maker", "maker"),
            "finalized" => ("Final review completed", "complete"),
            "pulled_back" when role == "reviewer" => ("Pulled back from final review", "checker"),
            _ => ($"Review: {action.Replace('_', ' ')}", TargetRoleForReview(review)),
        };
    }

    private static string InferActorRole(NdAnalysisReview review)
    {
        var action = review.Action?.Trim().ToLowerInvariant() ?? "";
        if (action is "submitted") return "maker";
        return review.ReviewerRole ?? "checker";
    }

    private static string? TargetRoleForReview(NdAnalysisReview review) =>
        review.Action?.Trim().ToLowerInvariant() switch
        {
            "submitted" => "checker",
            "approved" => review.ReviewerRole == "checker" ? "reviewer" : null,
            "finalized" => "complete",
            "pulled_back" => review.ReviewerRole == "checker" ? "maker" : "checker",
            _ => null,
        };

    private static string DescribeStatusChange(string? from, string to) =>
        $"Status: {FormatStatus(from)} → {FormatStatus(to)}";

    private static string FormatStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return "—";
        return status.Replace('_', ' ');
    }

    private static string? TargetRoleForStatus(string status) => status switch
    {
        "submitted_for_review" => "checker",
        "checker_approved" => "reviewer",
        "pulled_back" => "maker",
        "reviewer_approved" => "complete",
        _ => "maker",
    };

    private static string[] BuildStatusMeta(int totalGaps, int reviewedActionsBefore, int actionReviewsBefore)
    {
        var meta = new List<string>();
        if (totalGaps > 0)
        {
            meta.Add($"{totalGaps} gap{(totalGaps == 1 ? "" : "s")}");
            meta.Add($"{reviewedActionsBefore}/{totalGaps} reviewed");
        }
        if (actionReviewsBefore > 0)
            meta.Add($"{actionReviewsBefore} review entr{(actionReviewsBefore == 1 ? "y" : "ies")}");
        return meta.ToArray();
    }

    private static string[] BuildReviewMeta(
        NdAnalysisReview review,
        int totalGaps,
        int reviewedActionsBefore,
        int actionReviewsBefore)
    {
        var meta = new List<string>();
        if (totalGaps > 0)
        {
            meta.Add($"{totalGaps} gap{(totalGaps == 1 ? "" : "s")}");
            meta.Add($"{reviewedActionsBefore}/{totalGaps} reviewed");
        }
        if (actionReviewsBefore > 0)
            meta.Add($"{actionReviewsBefore} review entr{(actionReviewsBefore == 1 ? "y" : "ies")}");
        if (review.Priority is >= 0 and <= 100) meta.Add($"Priority {review.Priority}");
        if (!string.IsNullOrWhiteSpace(review.ReviewStatus))
            meta.Add($"Status {review.ReviewStatus.Replace('_', ' ')}");
        if (!string.IsNullOrWhiteSpace(review.Responsibility))
            meta.Add(review.Responsibility.Trim());
        return meta.ToArray();
    }
}
