using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Infrastructure.NewDashboard;
using Reguliq.Api.Services.Llm;
using Reguliq.Api.Services.NewDashboard;

namespace Reguliq.Api.Controllers.NewDashboard;

[ApiController]
[Route("nd/admin/prompts")]
public class AnalysisPromptsController(
    AppDbContext db,
    SupabaseJwtValidator jwt,
    NdAnalysisPromptVersionService promptVersions,
    NdPromptAiGenerationService aiGeneration,
    IConfiguration config) : NdControllerBase
{
    public record CreateSuggestionRequest(string PromptKey, string Comment);
    public record UpdateSuggestionRequest(string Comment);
    public record CreatePromptVersionRequest(
        string PromptKey,
        string PromptText,
        string? Label,
        List<Guid>? AppliedSuggestionIds);
    public record GeneratePromptRequest(
        string PromptKey,
        List<Guid> SuggestionIds,
        string Provider,
        string? Model,
        string? Instruction);

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(db, jwt, ct, "super_admin");
        if (error != null) return error;

        await promptVersions.EnsureSeededAsync(ct);

        var suggestions = await db.NdAnalysisPromptSuggestions.AsNoTracking()
            .OrderBy(s => s.PromptKey)
            .ThenBy(s => s.SortOrder)
            .ThenBy(s => s.CreatedAt)
            .ToListAsync(ct);

        var versionRows = await db.NdAnalysisPromptVersions.AsNoTracking()
            .OrderBy(v => v.PromptKey)
            .ThenByDescending(v => v.VersionNumber)
            .ToListAsync(ct);

        var authorIds = suggestions
            .SelectMany(s => new[] { s.CreatedBy, s.UpdatedBy })
            .Concat(versionRows.Select(v => v.CreatedBy))
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();

        var authorNames = authorIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.NdProfiles.AsNoTracking()
                .Where(p => authorIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, p => p.FullName ?? "Unknown", ct);

        var suggestionsByKey = suggestions
            .GroupBy(s => s.PromptKey)
            .ToDictionary(g => g.Key, g => g.Select(s => MapSuggestion(s, authorNames)).ToList());

        var versionsByKey = versionRows
            .GroupBy(v => v.PromptKey)
            .ToDictionary(g => g.Key, g => g.Select(v => MapVersion(v, authorNames)).ToList());

        var prompts = NdAnalysisPromptCatalog.AllPrompts
            .Select(p =>
            {
                var versions = versionsByKey.TryGetValue(p.Key, out var list) ? list : new List<object>();
                var current = versionRows.FirstOrDefault(v => v.PromptKey == p.Key && v.IsCurrent);
                return new
                {
                    p.Key,
                    p.Label,
                    p.Workflow,
                    p.Description,
                    text = current?.PromptText ?? p.Text,
                    currentVersionId = current?.Id,
                    versions,
                    suggestions = suggestionsByKey.TryGetValue(p.Key, out var sugList)
                        ? sugList
                        : new List<object>(),
                };
            })
            .ToList();

        var workflows = prompts
            .GroupBy(p => p.Workflow)
            .Select(g => new { workflow = g.Key, prompts = g.ToList() })
            .ToList();

        return Ok(new { success = true, data = new { workflows, prompts } });
    }

    [HttpGet("llm-providers")]
    public async Task<IActionResult> ListLlmProviders(CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(db, jwt, ct, "super_admin");
        if (error != null) return error;

        var providers = LlmProviderCatalog.Providers.Values
            .Select(def => new
            {
                id = def.Id,
                label = def.Label,
                models = def.Models,
                defaultModel = def.DefaultModel,
                apiKeyConfigured = IsApiKeyConfigured(def),
            })
            .ToList();

        return Ok(new { success = true, data = providers });
    }

    private bool IsApiKeyConfigured(LlmProviderDefinition def)
    {
        var fromConfig = config[def.ConfigKeyPath];
        if (!string.IsNullOrWhiteSpace(fromConfig)) return true;
        var fromEnv = Environment.GetEnvironmentVariable(def.EnvVarName);
        return !string.IsNullOrWhiteSpace(fromEnv);
    }

    [HttpPost("generate")]
    public async Task<IActionResult> GenerateVersion(
        [FromBody] GeneratePromptRequest body,
        CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(db, jwt, ct, "super_admin");
        if (error != null) return error;

        var promptKey = body.PromptKey?.Trim() ?? "";
        var promptDef = NdAnalysisPromptCatalog.Find(promptKey);
        if (promptDef == null)
            return BadRequest(new { success = false, message = "Unknown prompt key." });

        var suggestionIds = body.SuggestionIds ?? [];
        if (suggestionIds.Count == 0)
            return BadRequest(new { success = false, message = "Select at least one suggestion to apply." });

        if (string.IsNullOrWhiteSpace(body.Provider))
            return BadRequest(new { success = false, message = "Select an AI model to generate with." });

        try
        {
            var basePromptText = await promptVersions.GetCurrentTextAsync(promptKey, ct);
            var suggestions = await db.NdAnalysisPromptSuggestions.AsNoTracking()
                .Where(s => s.PromptKey == promptKey && suggestionIds.Contains(s.Id))
                .OrderBy(s => s.SortOrder)
                .ToListAsync(ct);

            if (suggestions.Count == 0)
                return BadRequest(new { success = false, message = "Selected suggestions were not found." });

            var result = await aiGeneration.GenerateAsync(
                basePromptText,
                suggestions,
                body.Provider.Trim(),
                body.Model,
                body.Instruction,
                ct);

            NdAnalysisPromptVersionService.ValidatePromptText(promptKey, result.PromptText);

            return Ok(new
            {
                success = true,
                data = new
                {
                    promptText = result.PromptText,
                    coverage = result.Coverage.Select(c => new { suggestionId = c.SuggestionId, covered = c.Covered }),
                },
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
        catch (HttpRequestException ex)
        {
            return BadRequest(new { success = false, message = $"AI provider request failed: {ex.Message}" });
        }
    }

    [HttpPost("versions")]
    public async Task<IActionResult> CreateVersion(
        [FromBody] CreatePromptVersionRequest body,
        CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, "super_admin");
        if (error != null) return error;

        try
        {
            var row = await promptVersions.CreateVersionAsync(
                body.PromptKey?.Trim() ?? "",
                body.PromptText ?? "",
                profile!.Id,
                body.Label,
                body.AppliedSuggestionIds,
                ct);
            var authorNames = await AuthorNamesAsync([profile.Id], ct);
            return Ok(new { success = true, data = MapVersion(row, authorNames) });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    [HttpPost("versions/{versionId:guid}/set-current")]
    public async Task<IActionResult> SetCurrentVersion(Guid versionId, CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(db, jwt, ct, "super_admin");
        if (error != null) return error;

        try
        {
            var row = await promptVersions.SetCurrentAsync(versionId, ct);
            var authorNames = row.CreatedBy.HasValue
                ? await AuthorNamesAsync([row.CreatedBy.Value], ct)
                : new Dictionary<Guid, string>();
            return Ok(new { success = true, data = MapVersion(row, authorNames) });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    [HttpPost("suggestions")]
    public async Task<IActionResult> CreateSuggestion(
        [FromBody] CreateSuggestionRequest body,
        CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, "super_admin");
        if (error != null) return error;

        var promptKey = body.PromptKey?.Trim() ?? "";
        if (NdAnalysisPromptCatalog.Find(promptKey) == null)
            return BadRequest(new { success = false, message = "Unknown prompt key." });

        var comment = body.Comment?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(comment))
            return BadRequest(new { success = false, message = "Suggestion comment is required." });

        var maxOrder = await db.NdAnalysisPromptSuggestions
            .Where(s => s.PromptKey == promptKey)
            .Select(s => (int?)s.SortOrder)
            .MaxAsync(ct) ?? -1;

        var row = new NdAnalysisPromptSuggestion
        {
            PromptKey = promptKey,
            Comment = comment,
            CreatedBy = profile!.Id,
            UpdatedBy = profile.Id,
            SortOrder = maxOrder + 1,
        };
        db.NdAnalysisPromptSuggestions.Add(row);
        await db.SaveChangesAsync(ct);

        var authorNames = await AuthorNamesAsync([profile.Id], ct);
        return Ok(new { success = true, data = MapSuggestion(row, authorNames) });
    }

    [HttpPut("suggestions/{suggestionId:guid}")]
    public async Task<IActionResult> UpdateSuggestion(
        Guid suggestionId,
        [FromBody] UpdateSuggestionRequest body,
        CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, "super_admin");
        if (error != null) return error;

        var row = await db.NdAnalysisPromptSuggestions.FirstOrDefaultAsync(s => s.Id == suggestionId, ct);
        if (row == null)
            return NotFound(new { success = false, message = "Suggestion not found." });

        var comment = body.Comment?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(comment))
            return BadRequest(new { success = false, message = "Suggestion comment is required." });

        row.Comment = comment;
        row.UpdatedBy = profile!.Id;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        var authorIds = new[] { row.CreatedBy, row.UpdatedBy }.Where(id => id.HasValue).Select(id => id!.Value).Distinct();
        var authorNames = await AuthorNamesAsync(authorIds, ct);
        return Ok(new { success = true, data = MapSuggestion(row, authorNames) });
    }

    [HttpDelete("suggestions/{suggestionId:guid}")]
    public async Task<IActionResult> DeleteSuggestion(Guid suggestionId, CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(db, jwt, ct, "super_admin");
        if (error != null) return error;

        var row = await db.NdAnalysisPromptSuggestions.FirstOrDefaultAsync(s => s.Id == suggestionId, ct);
        if (row == null)
            return NotFound(new { success = false, message = "Suggestion not found." });

        db.NdAnalysisPromptSuggestions.Remove(row);
        await db.SaveChangesAsync(ct);
        return Ok(new { success = true, message = "Suggestion deleted." });
    }

    private async Task<Dictionary<Guid, string>> AuthorNamesAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var idList = ids.Distinct().ToList();
        if (idList.Count == 0) return new Dictionary<Guid, string>();
        return await db.NdProfiles.AsNoTracking()
            .Where(p => idList.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.FullName ?? "Unknown", ct);
    }

    private static object MapVersion(NdAnalysisPromptVersion row, IReadOnlyDictionary<Guid, string> authorNames) => new
    {
        id = row.Id,
        promptKey = row.PromptKey,
        versionNumber = row.VersionNumber,
        label = row.Label,
        promptText = row.PromptText,
        isCurrent = row.IsCurrent,
        createdAt = row.CreatedAt,
        createdBy = row.CreatedBy,
        createdByName = row.CreatedBy.HasValue && authorNames.TryGetValue(row.CreatedBy.Value, out var name)
            ? name
            : null,
    };

    private static object MapSuggestion(NdAnalysisPromptSuggestion row, IReadOnlyDictionary<Guid, string> authorNames) => new
    {
        id = row.Id,
        promptKey = row.PromptKey,
        comment = row.Comment,
        sortOrder = row.SortOrder,
        createdAt = row.CreatedAt,
        updatedAt = row.UpdatedAt,
        createdBy = row.CreatedBy,
        createdByName = row.CreatedBy.HasValue && authorNames.TryGetValue(row.CreatedBy.Value, out var created)
            ? created
            : null,
        updatedBy = row.UpdatedBy,
        updatedByName = row.UpdatedBy.HasValue && authorNames.TryGetValue(row.UpdatedBy.Value, out var updated)
            ? updated
            : null,
        appliedInVersionId = row.AppliedInVersionId,
    };
}
