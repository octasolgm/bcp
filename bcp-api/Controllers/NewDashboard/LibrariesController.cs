using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Infrastructure.NewDashboard;
using Reguliq.Api.Services.NewDashboard;

namespace Reguliq.Api.Controllers.NewDashboard;

[ApiController]
[Route("nd/libraries")]
public class LibrariesController(
    AppDbContext db,
    SupabaseJwtValidator jwt,
    ILogger<LibrariesController> logger) : NdControllerBase
{
    public record LibraryPointInput(Guid RegulationPointId, Guid RegulationDocumentId, int DisplayOrder, object? PointSnapshot);
    public record CreateLibraryRequest(string Name, string? Description, Guid? DepartmentId, List<LibraryPointInput> Points);
    public record UpdateLibraryRequest(string Name, string? Description, Guid? DepartmentId, List<LibraryPointInput>? Points);

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] Guid? departmentId, CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(db, jwt, ct,
            "super_admin", "maker", "checker", "reviewer");
        if (error != null) return error;

        var q = db.NdLibraries.AsNoTracking().AsQueryable();
        if (departmentId.HasValue) q = q.Where(l => l.DepartmentId == departmentId);

        var libs = await q.OrderByDescending(l => l.CreatedAt).ToListAsync(ct);
        var pointCounts = await db.NdLibraryPoints.AsNoTracking()
            .GroupBy(lp => lp.LibraryId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var docCounts = await db.NdLibraryPoints.AsNoTracking()
            .GroupBy(lp => new { lp.LibraryId, lp.RegulationDocumentId })
            .Select(g => new { g.Key.LibraryId, g.Key.RegulationDocumentId })
            .ToListAsync(ct);

        return Ok(new
        {
            success = true,
            data = libs.Select(l =>
            {
                var pts = pointCounts.FirstOrDefault(c => c.Key == l.Id)?.Count ?? 0;
                var docs = docCounts.Where(d => d.LibraryId == l.Id).Select(d => d.RegulationDocumentId).Distinct().Count();
                return new
                {
                    id = l.Id,
                    name = l.Name,
                    description = l.Description,
                    departmentId = l.DepartmentId,
                    pointCount = pts,
                    documentCount = docs,
                    createdBy = l.CreatedBy,
                    createdAt = l.CreatedAt,
                };
            }),
        });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        try
        {
            var (_, error) = await RequireAuthAsync(db, jwt, ct,
                "super_admin", "maker", "checker", "reviewer");
            if (error != null) return error;

            var lib = await db.NdLibraries.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, ct);
            if (lib == null) return NotFound(new { success = false, message = "Not found" });

            var points = await db.NdLibraryPoints.AsNoTracking()
                .Where(lp => lp.LibraryId == id)
                .OrderBy(lp => lp.DisplayOrder)
                .ToListAsync(ct);

            return Ok(new
            {
                success = true,
                data = new
                {
                    id = lib.Id,
                    name = lib.Name,
                    description = lib.Description,
                    departmentId = lib.DepartmentId,
                    createdBy = lib.CreatedBy,
                    createdAt = lib.CreatedAt,
                    points = points.Select(p => new
                    {
                        regulationPointId = p.RegulationPointId,
                        regulationDocumentId = p.RegulationDocumentId,
                        displayOrder = p.DisplayOrder,
                        pointSnapshot = ParsePointSnapshot(p.PointSnapshot),
                    }),
                },
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load library {LibraryId}", id);
            return StatusCode(500, new { success = false, message = "Failed to load library" });
        }
    }

    private static object? ParsePointSnapshot(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            return JsonSerializer.Deserialize<JsonElement>(raw);
        }
        catch
        {
            return raw;
        }
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateLibraryRequest body, CancellationToken ct)
    {
        try
        {
            var (profile, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "maker");
            if (error != null) return error;

            var lib = new NdLibrary
            {
                Name = body.Name.Trim(),
                Description = body.Description,
                DepartmentId = body.DepartmentId,
                CreatedBy = profile!.Id,
            };
            db.NdLibraries.Add(lib);

            var prepared = await NdLibraryPointPersistence.PrepareAsync(
                db, profile.Id, ToSyncInputs(body.Points), ct);
            await db.SaveChangesAsync(ct);

            var order = 0;
            foreach (var p in prepared)
            {
                db.NdLibraryPoints.Add(new NdLibraryPoint
                {
                    LibraryId = lib.Id,
                    RegulationPointId = p.RegulationPointId,
                    RegulationDocumentId = p.RegulationDocumentId,
                    DisplayOrder = p.DisplayOrder > 0 ? p.DisplayOrder : order++,
                    PointSnapshot = p.PointSnapshotJson,
                });
            }

            await db.SaveChangesAsync(ct);
            return Ok(new { success = true, data = new { id = lib.Id } });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create library");
            return StatusCode(500, new { success = false, message = "Failed to create library" });
        }
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateLibraryRequest body, CancellationToken ct)
    {
        try
        {
            var (profile, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "maker");
            if (error != null) return error;

            var lib = await db.NdLibraries.FirstOrDefaultAsync(l => l.Id == id, ct);
            if (lib == null) return NotFound(new { success = false, message = "Not found" });
            if (profile!.Role != "super_admin" && lib.CreatedBy != profile.Id)
                return StatusCode(403, new { success = false, message = "Forbidden" });

            lib.Name = body.Name.Trim();
            lib.Description = body.Description;
            if (body.DepartmentId.HasValue) lib.DepartmentId = body.DepartmentId;
            lib.UpdatedAt = DateTimeOffset.UtcNow;

            if (body.Points != null)
            {
                var prepared = await NdLibraryPointPersistence.PrepareAsync(
                    db, profile.Id, ToSyncInputs(body.Points), ct);
                await db.SaveChangesAsync(ct);

                var existing = await db.NdLibraryPoints.Where(lp => lp.LibraryId == id).ToListAsync(ct);
                db.NdLibraryPoints.RemoveRange(existing);

                var order = 0;
                foreach (var p in prepared)
                {
                    db.NdLibraryPoints.Add(new NdLibraryPoint
                    {
                        LibraryId = id,
                        RegulationPointId = p.RegulationPointId,
                        RegulationDocumentId = p.RegulationDocumentId,
                        DisplayOrder = p.DisplayOrder > 0 ? p.DisplayOrder : order++,
                        PointSnapshot = p.PointSnapshotJson,
                    });
                }
            }

            await db.SaveChangesAsync(ct);
            return Ok(new { success = true });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
        catch (DbUpdateException ex)
        {
            logger.LogError(ex, "Failed to update library {LibraryId}", id);
            var inner = ex.InnerException?.Message ?? ex.Message;
            return BadRequest(new { success = false, message = $"Could not save library points: {inner}" });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update library {LibraryId}", id);
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    private static IEnumerable<LibraryPointSyncInput> ToSyncInputs(IEnumerable<LibraryPointInput> points) =>
        points.Select(p => new LibraryPointSyncInput(
            p.RegulationPointId,
            p.RegulationDocumentId,
            p.DisplayOrder,
            p.PointSnapshot));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        var lib = await db.NdLibraries.FirstOrDefaultAsync(l => l.Id == id, ct);
        if (lib == null) return NotFound(new { success = false, message = "Not found" });
        if (profile!.Role != "super_admin" && lib.CreatedBy != profile.Id)
            return StatusCode(403, new { success = false, message = "Forbidden" });

        db.NdLibraries.Remove(lib);
        await db.SaveChangesAsync(ct);
        return Ok(new { success = true, deleted = true });
    }
}
