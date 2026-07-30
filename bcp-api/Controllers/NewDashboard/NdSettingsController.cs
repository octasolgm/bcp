using Microsoft.AspNetCore.Mvc;
using Reguliq.Api.Data;
using Reguliq.Api.Infrastructure.NewDashboard;
using Reguliq.Api.Services.Llm;

namespace Reguliq.Api.Controllers.NewDashboard;

[ApiController]
[Route("nd/settings")]
public class NdSettingsController(
    AppDbContext db,
    SupabaseJwtValidator jwt,
    DualVerifyLlmSettingsService llmSettings,
    RegulWorkflowLlmSettingsService regulLlmSettings) : NdControllerBase
{
    [HttpGet("dual-verify-llm")]
    public async Task<IActionResult> GetActiveDualVerifyLlm(CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(db, jwt, ct);
        if (error != null) return error;

        var cfg = await llmSettings.GetConfigAsync(ct);
        var providerLabel = LlmProviderCatalog.TryGet(cfg.Provider, out var def)
            ? def.Label
            : cfg.Provider;

        return Ok(new
        {
            success = true,
            data = new
            {
                provider = cfg.Provider,
                model = cfg.Model,
                providerLabel,
            },
        });
    }

    [HttpGet("regul-workflow-llm")]
    public async Task<IActionResult> GetActiveRegulWorkflowLlm(CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(db, jwt, ct);
        if (error != null) return error;

        var cfg = await regulLlmSettings.GetConfigAsync(ct);
        var providerLabel = LlmProviderCatalog.TryGet(cfg.Provider, out var def)
            ? def.Label
            : cfg.Provider;

        return Ok(new
        {
            success = true,
            data = new
            {
                provider = cfg.Provider,
                model = cfg.Model,
                providerLabel,
            },
        });
    }
}
