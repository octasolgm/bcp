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
    DualVerifyLlmSettingsService llmSettings,
    RegulWorkflowLlmSettingsService regulLlmSettings,
    FinalizeEmbedLlmSettingsService finalizeEmbedLlmSettings,
    Microsoft.Extensions.Options.IOptions<Services.LocalDocs.AzureOpenAIOptions> azureOpenAiOptions) : NdControllerBase
{
    public record DualVerifyLlmUpdateRequest(string Provider, string Model);

    [HttpGet("dual-verify-llm")]
    public async Task<IActionResult> GetDualVerifyLlm(CancellationToken ct)
    {
        var (_, error) = await RequirePlatformAdminAsync(db, jwt, ct);
        if (error != null) return error;

        var view = await llmSettings.GetAdminViewAsync(ct);
        return Ok(new { success = true, data = view });
    }

    [HttpPut("dual-verify-llm")]
    public async Task<IActionResult> UpdateDualVerifyLlm(
        [FromBody] DualVerifyLlmUpdateRequest body,
        CancellationToken ct)
    {
        var (profile, error) = await RequirePlatformAdminAsync(db, jwt, ct);
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
        catch (Exception)
        {
            return BadRequest(new { success = false, message = "Could not save settings. Please try again." });
        }
    }

    [HttpGet("regul-workflow-llm")]
    public async Task<IActionResult> GetRegulWorkflowLlm(CancellationToken ct)
    {
        var (_, error) = await RequirePlatformAdminAsync(db, jwt, ct);
        if (error != null) return error;

        var view = await regulLlmSettings.GetAdminViewAsync(ct);
        return Ok(new { success = true, data = view });
    }

    [HttpGet("regul-retrieval-prompt-cache")]
    public async Task<IActionResult> GetRegulRetrievalPromptCache(CancellationToken ct)
    {
        var (_, error) = await RequirePlatformAdminAsync(db, jwt, ct);
        if (error != null) return error;
        return Ok(new { success = true, data = new { enabled = await regulLlmSettings.IsRetrievalPromptCacheEnabledAsync(ct) } });
    }

    [HttpPut("regul-retrieval-prompt-cache")]
    public async Task<IActionResult> UpdateRegulRetrievalPromptCache(
        [FromBody] RetrievalPromptCacheRequest body,
        CancellationToken ct)
    {
        var (profile, error) = await RequirePlatformAdminAsync(db, jwt, ct);
        if (error != null) return error;
        var enabled = await regulLlmSettings.SetRetrievalPromptCacheEnabledAsync(body.Enabled, profile.Id, ct);
        return Ok(new
        {
            success = true,
            data = new { enabled },
            message = enabled ? "Prompt caching enabled for the V5 pipeline." : "Prompt caching disabled for the V5 pipeline.",
        });
    }

    public sealed record RetrievalPromptCacheRequest(bool Enabled);

    [HttpGet("regul-pipeline-version")]
    public async Task<IActionResult> GetRegulPipelineVersion(CancellationToken ct)
    {
        var (_, error) = await RequirePlatformAdminAsync(db, jwt, ct);
        if (error != null) return error;
        return Ok(new
        {
            success = true,
            data = new
            {
                current = await regulLlmSettings.GetPipelineVersionAsync(ct),
                versions = Services.NewDashboard.NdRegulPipelineVersions.All,
            },
        });
    }

    [HttpPut("regul-pipeline-version")]
    public async Task<IActionResult> UpdateRegulPipelineVersion([FromBody] PipelineVersionRequest body, CancellationToken ct)
    {
        var (profile, error) = await RequirePlatformAdminAsync(db, jwt, ct);
        if (error != null) return error;
        if (!Services.NewDashboard.NdRegulPipelineVersions.IsKnown(body.Version))
            return BadRequest(new { success = false, message = $"Unknown pipeline version {body.Version}." });
        var version = await regulLlmSettings.SetPipelineVersionAsync(body.Version, profile.Id, ct);
        return Ok(new
        {
            success = true,
            data = new { current = version, versions = Services.NewDashboard.NdRegulPipelineVersions.All },
            message = $"New analyses and re-runs now use pipeline {Services.NewDashboard.NdRegulPipelineVersions.Label(version)}.",
        });
    }

    public sealed record PipelineVersionRequest(int Version);

    public sealed record EmbeddingProviderRequest(string Provider);

    private async Task<object> EmbeddingProviderViewAsync(CancellationToken ct)
    {
        var azure = azureOpenAiOptions.Value;
        return new
        {
            current = await regulLlmSettings.GetEmbeddingProviderAsync(ct),
            azureConfigured = Services.LocalDocs.PassageEmbeddingService.IsAzureConfigured(azure),
            azureDeployment = azure.EmbeddingDeployment,
        };
    }

    /// <summary>Embedding model for the search passages of pipeline v4+ (local bge-micro-v2 or Azure OpenAI).</summary>
    [HttpGet("regul-embedding-provider")]
    public async Task<IActionResult> GetRegulEmbeddingProvider(CancellationToken ct)
    {
        var (_, error) = await RequirePlatformAdminAsync(db, jwt, ct);
        if (error != null) return error;
        return Ok(new { success = true, data = await EmbeddingProviderViewAsync(ct) });
    }

    [HttpPut("regul-embedding-provider")]
    public async Task<IActionResult> UpdateRegulEmbeddingProvider([FromBody] EmbeddingProviderRequest body, CancellationToken ct)
    {
        var (profile, error) = await RequirePlatformAdminAsync(db, jwt, ct);
        if (error != null) return error;
        var provider = RegulWorkflowLlmSettingsService.NormalizeEmbeddingProvider(body.Provider);
        if (provider == null)
            return BadRequest(new { success = false, message = $"Unknown embedding provider '{body.Provider}'." });
        if (provider == RegulWorkflowLlmSettingsService.EmbeddingProviderAzureOpenAi
            && !Services.LocalDocs.PassageEmbeddingService.IsAzureConfigured(azureOpenAiOptions.Value))
            return BadRequest(new { success = false, message = "Azure OpenAI is not configured on this server (AzureOpenAI:Endpoint, ApiKey, EmbeddingDeployment)." });
        await regulLlmSettings.SetEmbeddingProviderAsync(provider, profile.Id, ct);
        return Ok(new
        {
            success = true,
            data = await EmbeddingProviderViewAsync(ct),
            message = provider == RegulWorkflowLlmSettingsService.EmbeddingProviderAzureOpenAi
                ? "Search passages now use Azure OpenAI embeddings. Existing documents are re-embedded in the background (no re-parse or re-extract)."
                : "Search passages now use the local model. Existing documents are re-embedded in the background (no re-parse or re-extract).",
        });
    }

    [HttpPut("regul-workflow-llm")]
    public async Task<IActionResult> UpdateRegulWorkflowLlm(
        [FromBody] DualVerifyLlmUpdateRequest body,
        CancellationToken ct)
    {
        var (profile, error) = await RequirePlatformAdminAsync(db, jwt, ct);
        if (error != null) return error;

        try
        {
            var view = await regulLlmSettings.SaveAsync(
                new DualVerifyLlmConfig(body.Provider, body.Model),
                profile.Id,
                ct);
            return Ok(new { success = true, data = view, message = "Regul workflow LLM settings saved." });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
        catch (Exception)
        {
            return BadRequest(new { success = false, message = "Could not save settings. Please try again." });
        }
    }

    [HttpGet("finalize-embed-llm")]
    public async Task<IActionResult> GetFinalizeEmbedLlm(CancellationToken ct)
    {
        var (_, error) = await RequirePlatformAdminAsync(db, jwt, ct);
        if (error != null) return error;

        var view = await finalizeEmbedLlmSettings.GetAdminViewAsync(ct);
        return Ok(new { success = true, data = view });
    }

    [HttpPut("finalize-embed-llm")]
    public async Task<IActionResult> UpdateFinalizeEmbedLlm(
        [FromBody] DualVerifyLlmUpdateRequest body,
        CancellationToken ct)
    {
        var (profile, error) = await RequirePlatformAdminAsync(db, jwt, ct);
        if (error != null) return error;

        try
        {
            var view = await finalizeEmbedLlmSettings.SaveAsync(
                new DualVerifyLlmConfig(body.Provider, body.Model),
                profile.Id,
                ct);
            return Ok(new { success = true, data = view, message = "Finalize embed LLM settings saved." });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
        catch (Exception)
        {
            return BadRequest(new { success = false, message = "Could not save settings. Please try again." });
        }
    }
}
