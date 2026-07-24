using Microsoft.AspNetCore.Mvc;
using Reguliq.Api.Data;
using Reguliq.Api.Infrastructure.NewDashboard;
using Reguliq.Api.Services.Llm;

namespace Reguliq.Api.Controllers.NewDashboard;

[ApiController]
[Route("nd/admin/settings")]
public class SystemSettingsController(
    AppDbContext db,
    SupabaseJwtValidator jwt,
    DualVerifyLlmSettingsService llmSettings) : NdControllerBase
{
    public record DualVerifyLlmUpdateRequest(string Provider, string Model);

    [HttpGet("dual-verify-llm")]
    public async Task<IActionResult> GetDualVerifyLlm(CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(db, jwt, ct, "super_admin");
        if (error != null) return error;

        var view = await llmSettings.GetAdminViewAsync(ct);
        return Ok(new { success = true, data = view });
    }

    [HttpPut("dual-verify-llm")]
    public async Task<IActionResult> UpdateDualVerifyLlm(
        [FromBody] DualVerifyLlmUpdateRequest body,
        CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, "super_admin");
        if (error != null) return error;

        try
        {
            var view = await llmSettings.SaveAsync(
                new DualVerifyLlmConfig(body.Provider, body.Model),
                profile.Id,
                ct);
            return Ok(new { success = true, data = view, message = "Dual verify LLM settings saved." });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
    }
}
