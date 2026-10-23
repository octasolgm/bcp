using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;

namespace Reguliq.Api.Infrastructure;

/// <summary>
/// Client installs only (deploy/client-package): brings a client's database to the analysis setup we run live -
/// the current analysis prompt versions and the analysis system settings (model, pipeline version, ...), exported
/// into client-baseline.json when the package is built. Without it a fresh client database would analyse with
/// the Base prompts and the catalog's default model.
///
/// Runs at startup when ClientBaseline:Enabled is true (set by the client installer) and the file is present; our
/// hosted environments set neither, so this is a no-op there. Each patch carries a newer baseline, so prompt and
/// setting improvements reach clients with the update. Anything the client's own admin changed (rows with
/// created_by / updated_by set) is left alone.
/// </summary>
public static class ClientBaselineSeeder
{
    public const string FileName = "client-baseline.json";

    private sealed record Baseline(Dictionary<string, JsonElement>? Settings, List<BaselinePrompt>? Prompts);

    private sealed record BaselinePrompt(string Key, int VersionNumber, string Label, string Text);

    public static async Task ApplyAsync(
        AppDbContext db,
        IConfiguration config,
        IWebHostEnvironment env,
        ILogger logger,
        CancellationToken ct)
    {
        if (!config.GetValue<bool>("ClientBaseline:Enabled")) return;
        var path = Path.Combine(env.ContentRootPath, FileName);
        if (!File.Exists(path))
        {
            logger.LogWarning("ClientBaseline:Enabled is set but {File} is missing; analysis setup not applied.", FileName);
            return;
        }

        var baseline = JsonSerializer.Deserialize<Baseline>(
            await File.ReadAllTextAsync(path, ct),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (baseline == null) return;

        var settingsChanged = 0;
        foreach (var (key, value) in baseline.Settings ?? [])
        {
            var json = value.GetRawText();
            var row = await db.NdSystemSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
            if (row == null)
            {
                db.NdSystemSettings.Add(new NdSystemSetting { Key = key, ValueJson = json, UpdatedAt = DateTimeOffset.UtcNow });
                settingsChanged++;
            }
            else if (row.UpdatedBy == null && !JsonEquals(row.ValueJson, json))
            {
                row.ValueJson = json;
                row.UpdatedAt = DateTimeOffset.UtcNow;
                settingsChanged++;
            }
        }

        var promptsChanged = 0;
        foreach (var prompt in baseline.Prompts ?? [])
        {
            var versions = await db.NdAnalysisPromptVersions.Where(v => v.PromptKey == prompt.Key).ToListAsync(ct);
            var current = versions.FirstOrDefault(v => v.IsCurrent);
            // A version the client's admin made stays current.
            var mayReplaceCurrent = current == null || current.CreatedBy == null;

            var match = versions.FirstOrDefault(v => v.PromptText == prompt.Text);
            if (match == null)
            {
                match = new NdAnalysisPromptVersion
                {
                    PromptKey = prompt.Key,
                    VersionNumber = versions.Count == 0 ? prompt.VersionNumber : versions.Max(v => v.VersionNumber) + 1,
                    Label = prompt.Label,
                    PromptText = prompt.Text,
                    IsCurrent = false,
                };
                db.NdAnalysisPromptVersions.Add(match);
                promptsChanged++;
            }

            if (mayReplaceCurrent && current != match)
            {
                if (current != null) current.IsCurrent = false;
                match.IsCurrent = true;
                promptsChanged++;
            }
        }

        if (settingsChanged + promptsChanged > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Client baseline applied: {Settings} setting(s), {Prompts} prompt change(s).", settingsChanged, promptsChanged);
        }
    }

    private static bool JsonEquals(string a, string b)
    {
        try
        {
            return JsonNode.DeepEquals(JsonNode.Parse(a), JsonNode.Parse(b));
        }
        catch (JsonException)
        {
            return a == b;
        }
    }
}
