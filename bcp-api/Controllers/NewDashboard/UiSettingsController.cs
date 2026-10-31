using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Infrastructure.NewDashboard;

namespace Reguliq.Api.Controllers.NewDashboard;

/// <summary>
/// Platform-wide display settings. Today: whether the analysis pipeline panel (the right-hand panel with
/// each pipeline step) is shown while an analysis runs. Off shows only a plain "Processing" status instead,
/// so no pipeline step names are visible. Everyone signed in can read it; only the platform admin changes it.
/// </summary>
[ApiController]
[Route("nd/ui-settings")]
public class UiSettingsController(AppDbContext db, SupabaseJwtValidator jwt) : NdControllerBase
{
    private const string SettingKey = "ui.pipeline_panel";

    public record UiSettingsRequest(bool PipelinePanelEnabled);

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "maker", "checker", "reviewer");
        if (error != null) return error;
        return Ok(new { success = true, data = new { pipelinePanelEnabled = await PipelinePanelEnabledAsync(ct) } });
    }

    [HttpPut]
    public async Task<IActionResult> Put([FromBody] UiSettingsRequest body, CancellationToken ct)
    {
        var (admin, error) = await RequirePlatformAdminAsync(db, jwt, ct);
        if (error != null) return error;

        var row = await db.NdSystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKey, ct);
        if (row == null)
        {
            row = new NdSystemSetting { Key = SettingKey };
            db.NdSystemSettings.Add(row);
        }
        row.ValueJson = JsonSerializer.Serialize(new { enabled = body.PipelinePanelEnabled });
        row.UpdatedAt = DateTimeOffset.UtcNow;
        row.UpdatedBy = admin.Id;
        await db.SaveChangesAsync(ct);

        return Ok(new { success = true, data = new { pipelinePanelEnabled = body.PipelinePanelEnabled } });
    }

    /// <summary>On by default (no row yet), which keeps today's behaviour.</summary>
    private async Task<bool> PipelinePanelEnabledAsync(CancellationToken ct)
    {
        var json = await db.NdSystemSettings.AsNoTracking()
            .Where(s => s.Key == SettingKey)
            .Select(s => s.ValueJson)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(json)) return true;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return !doc.RootElement.TryGetProperty("enabled", out var v) || v.ValueKind != JsonValueKind.False;
        }
        catch (JsonException)
        {
            return true;
        }
    }
}
