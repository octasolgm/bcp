using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Infrastructure.NewDashboard;

namespace Reguliq.Api.Controllers.NewDashboard;

[ApiController]
[Route("nd/results/{runId:guid}/points/{pointId:guid}/temp-review-comments")]
public class TempPointReviewCommentsController(
    AppDbContext db,
    SupabaseJwtValidator jwt) : NdControllerBase
{
    public record AddTempReviewCommentRequest(string Comment);
    public record UpdateTempReviewCommentRequest(string Comment);

    [HttpGet]
    public async Task<IActionResult> List(Guid runId, Guid pointId, CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct,
            "super_admin", "maker", "checker", "reviewer");
        if (error != null) return error;

        var point = await RequirePointAsync(runId, pointId, profile!, ct);
        if (point == null) return NotFound(new { success = false, message = "Point not found." });

        var rows = await db.NdTempPointReviewComments.AsNoTracking()
            .Where(c => c.AnalysisPointId == pointId)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(ct);

        var authorIds = rows.Where(r => r.CommentedBy.HasValue).Select(r => r.CommentedBy!.Value).Distinct().ToList();
        var authors = authorIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.NdProfiles.AsNoTracking()
                .Where(p => authorIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, p => p.FullName, ct);

        return Ok(new
        {
            success = true,
            data = rows.Select(r => MapRow(r, authors)),
        });
    }

    [HttpPost]
    public async Task<IActionResult> Add(
        Guid runId,
        Guid pointId,
        [FromBody] AddTempReviewCommentRequest body,
        CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct,
            "super_admin", "maker", "checker", "reviewer");
        if (error != null) return error;

        var point = await RequirePointAsync(runId, pointId, profile!, ct);
        if (point == null) return NotFound(new { success = false, message = "Point not found." });

        var text = body.Comment?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(text))
            return BadRequest(new { success = false, message = "Comment cannot be empty." });

        var row = new NdTempPointReviewComment
        {
            AnalysisPointId = pointId,
            Comment = text,
            CommentedBy = profile!.Id,
        };
        db.NdTempPointReviewComments.Add(row);
        await db.SaveChangesAsync(ct);

        var authors = new Dictionary<Guid, string> { [profile.Id] = profile.FullName };
        return Ok(new { success = true, data = MapRow(row, authors) });
    }

    [HttpPut("{commentId:guid}")]
    public async Task<IActionResult> Update(
        Guid runId,
        Guid pointId,
        Guid commentId,
        [FromBody] UpdateTempReviewCommentRequest body,
        CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct,
            "super_admin", "maker", "checker", "reviewer");
        if (error != null) return error;

        var point = await RequirePointAsync(runId, pointId, profile!, ct);
        if (point == null) return NotFound(new { success = false, message = "Point not found." });

        var row = await db.NdTempPointReviewComments
            .FirstOrDefaultAsync(c => c.Id == commentId && c.AnalysisPointId == pointId, ct);
        if (row == null) return NotFound(new { success = false, message = "Comment not found." });

        if (profile!.Role != "super_admin" && row.CommentedBy != profile.Id)
            return StatusCode(403, new { success = false, message = "You can only edit your own comments." });

        var text = body.Comment?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(text))
            return BadRequest(new { success = false, message = "Comment cannot be empty." });

        row.Comment = text;
        await db.SaveChangesAsync(ct);

        var authors = new Dictionary<Guid, string> { [profile.Id] = profile.FullName };
        if (row.CommentedBy.HasValue && row.CommentedBy != profile.Id)
        {
            var author = await db.NdProfiles.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == row.CommentedBy, ct);
            if (author != null) authors[row.CommentedBy.Value] = author.FullName;
        }

        return Ok(new { success = true, data = MapRow(row, authors) });
    }

    [HttpDelete("{commentId:guid}")]
    public async Task<IActionResult> Delete(
        Guid runId,
        Guid pointId,
        Guid commentId,
        CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct,
            "super_admin", "maker", "checker", "reviewer");
        if (error != null) return error;

        var point = await RequirePointAsync(runId, pointId, profile!, ct);
        if (point == null) return NotFound(new { success = false, message = "Point not found." });

        var row = await db.NdTempPointReviewComments
            .FirstOrDefaultAsync(c => c.Id == commentId && c.AnalysisPointId == pointId, ct);
        if (row == null) return NotFound(new { success = false, message = "Comment not found." });

        if (profile!.Role != "super_admin" && row.CommentedBy != profile.Id)
            return StatusCode(403, new { success = false, message = "You can only delete your own comments." });

        db.NdTempPointReviewComments.Remove(row);
        await db.SaveChangesAsync(ct);
        return Ok(new { success = true });
    }

    private async Task<NdAnalysisPoint?> RequirePointAsync(
        Guid runId,
        Guid pointId,
        NdProfile profile,
        CancellationToken ct)
    {
        var run = await db.NdAnalysisRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run == null) return null;
        if (profile.Role == "maker" && run.CreatedBy != profile.Id) return null;

        return await db.NdAnalysisPoints.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == pointId && p.AnalysisRunId == runId, ct);
    }

    private static object MapRow(NdTempPointReviewComment row, IReadOnlyDictionary<Guid, string> authors) =>
        new
        {
            id = row.Id,
            analysisPointId = row.AnalysisPointId,
            comment = row.Comment,
            commentedBy = row.CommentedBy,
            commentedByName = row.CommentedBy.HasValue && authors.TryGetValue(row.CommentedBy.Value, out var name)
                ? name
                : null,
            createdAt = row.CreatedAt,
        };
}
