using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Infrastructure.NewDashboard;

namespace Reguliq.Api.Controllers.NewDashboard;

/// <summary>
/// Platform Support inbox: the platform super admin reads and answers support tickets from every workspace.
/// Reads cross workspaces on purpose (IgnoreQueryFilters), so every endpoint is platform-admin only.
/// </summary>
[ApiController]
[Route("nd/admin/support")]
public class SupportInboxController(AppDbContext db, SupabaseJwtValidator jwt) : NdControllerBase
{
    public record StaffMessageRequest(string? Body, bool? Internal);

    public record UpdateTicketRequest(string? Status, string? Priority);

    private IQueryable<NdSupportTicket> AllTickets => db.NdSupportTickets.IgnoreQueryFilters();

    [HttpGet("tickets")]
    public async Task<IActionResult> List(
        [FromQuery] string? status,
        [FromQuery] Guid? workspaceId,
        [FromQuery] string? category,
        [FromQuery] string? priority,
        [FromQuery] string? q,
        CancellationToken ct)
    {
        var (_, error) = await RequirePlatformAdminAsync(db, jwt, ct);
        if (error != null) return error;

        var query = AllTickets.AsNoTracking();
        if (status == "active")
            query = query.Where(t => t.Status == SupportStatuses.Open || t.Status == SupportStatuses.WaitingOnClient);
        else if (!string.IsNullOrWhiteSpace(status) && SupportStatuses.All.Contains(status))
            query = query.Where(t => t.Status == status);
        if (workspaceId is Guid ws)
            query = query.Where(t => t.TenantId == ws);
        if (!string.IsNullOrWhiteSpace(category) && SupportCategories.All.Contains(category))
            query = query.Where(t => t.Category == category);
        if (!string.IsNullOrWhiteSpace(priority) && SupportPriorities.All.Contains(priority))
            query = query.Where(t => t.Priority == priority);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            var pattern = $"%{term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";
            var digits = new string(term.Where(char.IsDigit).ToArray());
            long? no = digits.Length is > 0 and < 18 ? long.Parse(digits) : null;
            query = query.Where(t => EF.Functions.ILike(t.Subject, pattern)
                || (t.CreatedByName != null && EF.Functions.ILike(t.CreatedByName, pattern))
                || (t.CreatedByEmail != null && EF.Functions.ILike(t.CreatedByEmail, pattern))
                || (no != null && t.TicketNo == no));
        }

        var rows = await query.OrderByDescending(t => t.LastMessageAt).Take(500).ToListAsync(ct);
        var names = await WorkspaceNamesAsync(rows.Select(r => r.TenantId), ct);

        return Ok(new
        {
            success = true,
            data = rows.Select(t => SupportMapping.Ticket(t, forStaff: true, WorkspaceName(names, t.TenantId))),
        });
    }

    /// <summary>Counts for the menu badge: tickets with an unread client message, and tickets still active.</summary>
    [HttpGet("unread")]
    public async Task<IActionResult> Unread(CancellationToken ct)
    {
        var (_, error) = await RequirePlatformAdminAsync(db, jwt, ct);
        if (error != null) return error;

        var count = await AllTickets.CountAsync(t => t.LastClientMessageAt != null
            && (t.StaffLastReadAt == null || t.LastClientMessageAt > t.StaffLastReadAt), ct);
        var open = await AllTickets.CountAsync(t => t.Status == SupportStatuses.Open, ct);
        return Ok(new { success = true, data = new { count, open } });
    }

    /// <summary>Workspaces that have sent at least one ticket, for the inbox filter.</summary>
    [HttpGet("workspaces")]
    public async Task<IActionResult> Workspaces(CancellationToken ct)
    {
        var (_, error) = await RequirePlatformAdminAsync(db, jwt, ct);
        if (error != null) return error;

        var ids = await AllTickets.Where(t => t.TenantId != null).Select(t => t.TenantId).Distinct().ToListAsync(ct);
        var names = await WorkspaceNamesAsync(ids, ct);
        return Ok(new
        {
            success = true,
            data = names.OrderBy(n => n.Value).Select(n => new { id = n.Key, name = n.Value }),
        });
    }

    [HttpGet("tickets/{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var (_, error) = await RequirePlatformAdminAsync(db, jwt, ct);
        if (error != null) return error;

        var ticket = await AllTickets.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (ticket == null) return NotFound(new { success = false, message = "Ticket not found." });

        var messages = await db.NdSupportMessages.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.TicketId == id)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(ct);

        ticket.StaffLastReadAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        var names = await WorkspaceNamesAsync([ticket.TenantId], ct);
        return Ok(new
        {
            success = true,
            data = new
            {
                ticket = SupportMapping.Ticket(ticket, forStaff: true, WorkspaceName(names, ticket.TenantId)),
                messages = messages.Select(m => SupportMapping.Message(m, forStaff: true)),
            },
        });
    }

    [HttpPost("tickets/{id:guid}/messages")]
    public async Task<IActionResult> Reply(Guid id, [FromBody] StaffMessageRequest body, CancellationToken ct)
    {
        var (profile, error) = await RequirePlatformAdminAsync(db, jwt, ct);
        if (error != null) return error;

        var text = body.Body?.Trim() ?? "";
        if (text.Length == 0 || text.Length > SupportMapping.MaxBody)
            return BadRequest(new { success = false, message = $"Message must be 1 to {SupportMapping.MaxBody} characters." });

        var ticket = await AllTickets.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (ticket == null) return NotFound(new { success = false, message = "Ticket not found." });

        var isInternal = body.Internal == true;
        var now = DateTimeOffset.UtcNow;
        var message = new NdSupportMessage
        {
            TicketId = ticket.Id,
            // The ticket's workspace owns the message, not the admin's own workspace.
            TenantId = ticket.TenantId,
            AuthorId = profile.Id,
            AuthorName = profile.FullName,
            IsStaff = true,
            IsInternal = isInternal,
            Body = text,
            CreatedAt = now,
        };
        db.NdSupportMessages.Add(message);

        ticket.StaffLastReadAt = now;
        ticket.UpdatedAt = now;
        if (!isInternal)
        {
            ticket.LastMessageAt = now;
            ticket.LastStaffMessageAt = now;
            if (ticket.Status == SupportStatuses.Open) ticket.Status = SupportStatuses.WaitingOnClient;
        }
        await db.SaveChangesAsync(ct);

        return Ok(new { success = true, data = SupportMapping.Message(message, forStaff: true) });
    }

    [HttpPut("tickets/{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateTicketRequest body, CancellationToken ct)
    {
        var (_, error) = await RequirePlatformAdminAsync(db, jwt, ct);
        if (error != null) return error;

        var ticket = await AllTickets.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (ticket == null) return NotFound(new { success = false, message = "Ticket not found." });

        var status = body.Status?.Trim().ToLowerInvariant();
        var priority = body.Priority?.Trim().ToLowerInvariant();
        if (status != null && !SupportStatuses.All.Contains(status))
            return BadRequest(new { success = false, message = "Unknown status." });
        if (priority != null && !SupportPriorities.All.Contains(priority))
            return BadRequest(new { success = false, message = "Unknown priority." });

        if (status != null) ticket.Status = status;
        if (priority != null) ticket.Priority = priority;
        ticket.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        var names = await WorkspaceNamesAsync([ticket.TenantId], ct);
        return Ok(new { success = true, data = SupportMapping.Ticket(ticket, forStaff: true, WorkspaceName(names, ticket.TenantId)) });
    }

    private async Task<Dictionary<Guid, string>> WorkspaceNamesAsync(IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var list = ids.Where(i => i.HasValue).Select(i => i!.Value).Distinct().ToList();
        if (list.Count == 0) return new Dictionary<Guid, string>();
        return await db.NdWorkspaces.AsNoTracking()
            .Where(w => list.Contains(w.Id))
            .ToDictionaryAsync(w => w.Id, w => w.Name, ct);
    }

    private static string? WorkspaceName(IReadOnlyDictionary<Guid, string> names, Guid? id) =>
        id is Guid g && names.TryGetValue(g, out var name) ? name : null;
}
