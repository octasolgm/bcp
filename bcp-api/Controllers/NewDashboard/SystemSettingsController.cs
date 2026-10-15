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
    Microsoft.Extensions.Options.IOptions<Services.LocalDocs.AzureOpenAIOptions> azureOpenAiOptions,
    Microsoft.Extensions.Options.IOptions<Services.LocalDocs.AzureDocumentIntelligenceOptions> azureDocIntelligenceOptions,
    IConfiguration configuration) : NdControllerBase
{
    public record DualVerifyLlmUpdateRequest(string Provider, string Model);

    /// <summary>
    /// What this installation currently lets leave the server, so a security reviewer can check it without reading
    /// configuration files: OpenRouter data rules, the full-text qualitative review, the outbound allow-list, the
    /// embedding choice and the document-reading service. Host names only, never keys.
    /// </summary>
    [HttpGet("privacy-posture")]
    public async Task<IActionResult> PrivacyPosture(CancellationToken ct)
    {
        var (_, error) = await RequirePlatformAdminAsync(db, jwt, ct);
        if (error != null) return error;

        var privacy = LlmPrivacySettings.From(configuration);
        var embedding = await regulLlmSettings.GetEmbeddingProviderAsync(ct);
        var azureDi = azureDocIntelligenceOptions.Value;
        Uri.TryCreate(azureDi.Endpoint, UriKind.Absolute, out var azureDiUri);
        Uri.TryCreate(azureOpenAiOptions.Value.Endpoint, UriKind.Absolute, out var azureOpenAiUri);

        return Ok(new
        {
            success = true,
            data = new
            {
                openRouter = new
                {
                    zeroDataRetention = privacy.OpenRouterZeroDataRetention,
                    denyDataCollection = privacy.OpenRouterDenyDataCollection,
                    sentWithEveryRequest = privacy.OpenRouterProviderPreferences(),
                },
                qualitativeAssessmentDisabled = privacy.DisableQualitativeAssessment,
                outbound = new
                {
                    restricted = privacy.OutboundRestricted,
                    allowedHosts = privacy.AllowedOutboundHosts,
                    requireHttps = privacy.RequireHttpsOutbound,
                    insecureHostsAllowed = privacy.AllowInsecureHosts,
                },
                embeddingProvider = embedding,
                documentReading = new
                {
                    azureDocumentIntelligenceConfigured = azureDiUri != null && !string.IsNullOrWhiteSpace(azureDi.ApiKey),
                    azureDocumentIntelligenceHost = azureDiUri?.Host,
                    usesHttps = azureDiUri?.Scheme == Uri.UriSchemeHttps,
                    note = "The processing region is a property of the Azure resource: check it in the Azure portal (Overview, Location).",
                },
                azureOpenAi = new
                {
                    configured = azureOpenAiUri != null && !string.IsNullOrWhiteSpace(azureOpenAiOptions.Value.ApiKey),
                    host = azureOpenAiUri?.Host,
                },
            },
        });
    }

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

    /// <summary>Pipeline v5+: skip the gap re-check for low-materiality gaps (on by default).</summary>
    [HttpGet("regul-gap-check-skip-low")]
    public async Task<IActionResult> GetRegulGapCheckSkipLow(CancellationToken ct)
    {
        var (_, error) = await RequirePlatformAdminAsync(db, jwt, ct);
        if (error != null) return error;
        return Ok(new { success = true, data = new { enabled = await regulLlmSettings.IsGapCheckSkipLowEnabledAsync(ct) } });
    }

    [HttpPut("regul-gap-check-skip-low")]
    public async Task<IActionResult> UpdateRegulGapCheckSkipLow([FromBody] RetrievalPromptCacheRequest body, CancellationToken ct)
    {
        var (profile, error) = await RequirePlatformAdminAsync(db, jwt, ct);
        if (error != null) return error;
        var enabled = await regulLlmSettings.SetGapCheckSkipLowAsync(body.Enabled, profile.Id, ct);
        return Ok(new
        {
            success = true,
            data = new { enabled },
            message = enabled
                ? "Low-risk gaps are no longer re-checked (saves one AI call per low gap)."
                : "Every gap is re-checked again.",
        });
    }

    /// <summary>Sound when an analysis finishes: off / tone / voice. Every signed-in user reads it (the browser
    /// plays it); only the platform super admin changes it.</summary>
    [HttpGet("analysis-finish-sound")]
    public async Task<IActionResult> GetAnalysisFinishSound(CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(db, jwt, ct);
        if (error != null) return error;
        return Ok(new { success = true, data = new { mode = await regulLlmSettings.GetFinishSoundModeAsync(ct) } });
    }

    public sealed record AnalysisFinishSoundRequest(string Mode);

    [HttpPut("analysis-finish-sound")]
    public async Task<IActionResult> UpdateAnalysisFinishSound([FromBody] AnalysisFinishSoundRequest body, CancellationToken ct)
    {
        var (profile, error) = await RequirePlatformAdminAsync(db, jwt, ct);
        if (error != null) return error;
        if (RegulWorkflowLlmSettingsService.NormalizeFinishSoundMode(body.Mode) == null)
            return BadRequest(new { success = false, message = "Choose off, tone or voice." });
        var mode = await regulLlmSettings.SetFinishSoundModeAsync(body.Mode, profile.Id, ct);
        return Ok(new
        {
            success = true,
            data = new { mode },
            message = mode switch
            {
                RegulWorkflowLlmSettingsService.FinishSoundVoice => "A chime and a spoken summary play when an analysis finishes.",
                RegulWorkflowLlmSettingsService.FinishSoundTone => "A chime plays when an analysis finishes.",
                _ => "No sound when an analysis finishes.",
            },
        });
    }

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
