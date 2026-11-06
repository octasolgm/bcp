using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Infrastructure.NewDashboard;
using Reguliq.Api.Services.NewDashboard.Demo;

namespace Reguliq.Api.Controllers.NewDashboard;

/// <summary>
/// Contact support for workspace users: open a ticket, chat on it with the platform team, mark it resolved.
/// Everything here is scoped to the caller's workspace by the tenant filter. A workspace admin sees all of the
/// workspace's tickets; other roles see the ones they opened. Demo accounts never get support (fixed simulation).
/// </summary>
[ApiController]
[Route("nd/support")]
public class SupportController(
    AppDbContext db,
    SupabaseJwtValidator jwt,
    NdDemoUserDirectory demoDirectory) : NdControllerBase
{
    public record CreateTicketRequest(string? Subject, string? Category, string? Priority, string? Message);

    public record MessageRequest(string? Body);

    public record StatusRequest(string? Status);

    [HttpGet("tickets")]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var (profile, _, error) = await RequireSupportUserAsync(ct);
        if (error != null) return error;

        var rows = await VisibleTickets(profile)
            .AsNoTracking()
            .OrderByDescending(t => t.LastMessageAt)
            .Take(500)
            .ToListAsync(ct);

        return Ok(new { success = true, data = rows.Select(t => SupportMapping.Ticket(t, forStaff: false)) });
    }

    [HttpGet("unread")]
    public async Task<IActionResult> Unread(CancellationToken ct)
    {
        var (profile, _, error) = await RequireSupportUserAsync(ct);
        if (error != null) return error;

        var count = await VisibleTickets(profile)
            .CountAsync(t => t.LastStaffMessageAt != null
                && (t.ClientLastReadAt == null || t.LastStaffMessageAt > t.ClientLastReadAt), ct);
        return Ok(new { success = true, data = new { count } });
    }

    [HttpPost("tickets")]
    public async Task<IActionResult> Create([FromBody] CreateTicketRequest body, CancellationToken ct)
    {
        var (profile, user, error) = await RequireSupportUserAsync(ct);
        if (error != null) return error;

        var subject = body.Subject?.Trim() ?? "";
        var message = body.Message?.Trim() ?? "";
        var category = (body.Category ?? SupportCategories.Question).Trim().ToLowerInvariant();
        var priority = (body.Priority ?? SupportPriorities.Normal).Trim().ToLowerInvariant();

        if (subject.Length < 3 || subject.Length > SupportMapping.MaxSubject)
            return BadRequest(new { success = false, message = $"Subject must be 3 to {SupportMapping.MaxSubject} characters." });
        if (message.Length == 0 || message.Length > SupportMapping.MaxBody)
            return BadRequest(new { success = false, message = $"Message must be 1 to {SupportMapping.MaxBody} characters." });
        if (!SupportCategories.All.Contains(category))
            return BadRequest(new { success = false, message = "Unknown request type." });
        if (!SupportPriorities.All.Contains(priority))
            return BadRequest(new { success = false, message = "Unknown priority." });

        var now = DateTimeOffset.UtcNow;
        var ticket = new NdSupportTicket
        {
            Subject = subject,
            Category = category,
            Priority = priority,
            Status = SupportStatuses.Open,
            CreatedBy = profile.Id,
            CreatedByName = profile.FullName,
            CreatedByEmail = user.Email,
            CreatedAt = now,
            UpdatedAt = now,
            LastMessageAt = now,
            LastClientMessageAt = now,
            ClientLastReadAt = now,
        };
        db.NdSupportTickets.Add(ticket);
        db.NdSupportMessages.Add(new NdSupportMessage
        {
            TicketId = ticket.Id,
            AuthorId = profile.Id,
            AuthorName = profile.FullName,
            Body = message,
            CreatedAt = now,
        });
        await db.SaveChangesAsync(ct);

        return Ok(new { success = true, data = SupportMapping.Ticket(ticket, forStaff: false) });
    }

    [HttpGet("tickets/{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var (profile, _, error) = await RequireSupportUserAsync(ct);
        if (error != null) return error;

        var ticket = await VisibleTickets(profile).FirstOrDefaultAsync(t => t.Id == id, ct);
        if (ticket == null) return NotFound(new { success = false, message = "Ticket not found." });

        var messages = await db.NdSupportMessages.AsNoTracking()
            .Where(m => m.TicketId == id && !m.IsInternal)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(ct);

        ticket.ClientLastReadAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return Ok(new
        {
            success = true,
            data = new
            {
                ticket = SupportMapping.Ticket(ticket, forStaff: false),
                messages = messages.Select(m => SupportMapping.Message(m, forStaff: false)),
            },
        });
    }

    [HttpPost("tickets/{id:guid}/messages")]
    public async Task<IActionResult> Reply(Guid id, [FromBody] MessageRequest body, CancellationToken ct)
    {
        var (profile, _, error) = await RequireSupportUserAsync(ct);
        if (error != null) return error;

        var text = body.Body?.Trim() ?? "";
        if (text.Length == 0 || text.Length > SupportMapping.MaxBody)
            return BadRequest(new { success = false, message = $"Message must be 1 to {SupportMapping.MaxBody} characters." });

        var ticket = await VisibleTickets(profile).FirstOrDefaultAsync(t => t.Id == id, ct);
        if (ticket == null) return NotFound(new { success = false, message = "Ticket not found." });
        if (ticket.Status == SupportStatuses.Closed)
            return BadRequest(new { success = false, message = "This ticket is closed. Please open a new request." });

        var now = DateTimeOffset.UtcNow;
        var message = new NdSupportMessage
        {
            TicketId = ticket.Id,
            TenantId = ticket.TenantId,
            AuthorId = profile.Id,
            AuthorName = profile.FullName,
            Body = text,
            CreatedAt = now,
        };
        db.NdSupportMessages.Add(message);
        // A client reply puts the ticket back in the support team's queue.
        ticket.Status = SupportStatuses.Open;
        ticket.LastMessageAt = now;
        ticket.LastClientMessageAt = now;
        ticket.ClientLastReadAt = now;
        ticket.UpdatedAt = now;
        await db.SaveChangesAsync(ct);

        return Ok(new { success = true, data = SupportMapping.Message(message, forStaff: false) });
    }

    /// <summary>The workspace can mark its own ticket resolved, or reopen it.</summary>
    [HttpPut("tickets/{id:guid}/status")]
    public async Task<IActionResult> SetStatus(Guid id, [FromBody] StatusRequest body, CancellationToken ct)
    {
        var (profile, _, error) = await RequireSupportUserAsync(ct);
        if (error != null) return error;

        var status = body.Status?.Trim().ToLowerInvariant();
        if (status != SupportStatuses.Resolved && status != SupportStatuses.Open)
            return BadRequest(new { success = false, message = "You can mark a ticket resolved or reopen it." });

        var ticket = await VisibleTickets(profile).FirstOrDefaultAsync(t => t.Id == id, ct);
        if (ticket == null) return NotFound(new { success = false, message = "Ticket not found." });
        if (ticket.Status == SupportStatuses.Closed)
            return BadRequest(new { success = false, message = "This ticket is closed. Please open a new request." });

        ticket.Status = status;
        ticket.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Ok(new { success = true, data = SupportMapping.Ticket(ticket, forStaff: false) });
    }

    private IQueryable<NdSupportTicket> VisibleTickets(NdProfile profile)
    {
        var q = db.NdSupportTickets.AsQueryable();
        // Workspace admins see every ticket of their workspace; other roles only their own.
        if (!string.Equals(profile.Role, "super_admin", StringComparison.OrdinalIgnoreCase))
            q = q.Where(t => t.CreatedBy == profile.Id);
        return q;
    }

    private async Task<(NdProfile Profile, JwtUser User, IActionResult? Error)> RequireSupportUserAsync(CancellationToken ct)
    {
        var (profile, user, error) = await RequireAuthWithUserAsync(db, jwt, ct,
            "super_admin", "maker", "checker", "reviewer");
        if (error != null) return (null!, null!, error);

        var isDemo = NdDemoIsolationHelper.IsDemoEmail(user.Email)
            || NdDemoIsolationHelper.IsDemoName(profile.FullName)
            || await demoDirectory.IsDemoProfileAsync(profile.Id, ct);
        if (isDemo)
            return (null!, null!, StatusCode(403, new { success = false, message = "Support is not available for demo accounts." }));

        return (profile, user, null);
    }
}

/// <summary>Response shapes shared by the workspace support endpoints and the platform Support inbox.</summary>
internal static class SupportMapping
{
    public const int MaxSubject = 200;
    public const int MaxBody = 5000;

    public static string Number(long no) => $"SUP-{no:D5}";

    public static bool UnreadForClient(NdSupportTicket t) =>
        t.LastStaffMessageAt != null && (t.ClientLastReadAt == null || t.LastStaffMessageAt > t.ClientLastReadAt);

    public static bool UnreadForStaff(NdSupportTicket t) =>
        t.LastClientMessageAt != null && (t.StaffLastReadAt == null || t.LastClientMessageAt > t.StaffLastReadAt);

    public static object Ticket(NdSupportTicket t, bool forStaff, string? workspaceName = null) => new
    {
        id = t.Id,
        number = Number(t.TicketNo),
        subject = t.Subject,
        category = t.Category,
        priority = t.Priority,
        status = t.Status,
        createdBy = t.CreatedBy,
        createdByName = t.CreatedByName,
        createdByEmail = forStaff ? t.CreatedByEmail : null,
        createdAt = t.CreatedAt,
        updatedAt = t.UpdatedAt,
        lastMessageAt = t.LastMessageAt,
        unread = forStaff ? UnreadForStaff(t) : UnreadForClient(t),
        workspaceId = forStaff ? t.TenantId : null,
        workspaceName = forStaff ? workspaceName : null,
    };

    public static object Message(NdSupportMessage m, bool forStaff) => new
    {
        id = m.Id,
        body = m.Body,
        isStaff = m.IsStaff,
        isInternal = forStaff && m.IsInternal,
        // The workspace sees replies as coming from the support team, not from a named person.
        authorName = m.IsStaff && !forStaff ? "Comply Solutions Support" : m.AuthorName,
        createdAt = m.CreatedAt,
    };
}
