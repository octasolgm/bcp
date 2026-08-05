using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;

namespace Reguliq.Api.Services.NewDashboard;

public class NdAnalysisPromptVersionService(AppDbContext db)
{
    public const string JudgmentSystemKey = "regul_judgment_system";
    public const string JudgmentUserContextKey = "regul_judgment_user_context";
    public const string JudgmentUserQueryKey = "regul_judgment_user_query";
    public const string JudgmentFullSystemKey = "regul_judgment_full_system";
    public const string JudgmentFullUserContextKey = "regul_judgment_full_user_context";
    public const string JudgmentFullUserQueryKey = "regul_judgment_full_user_query";
    public const int JudgmentSemanticV2VersionNumber = 2;
    public const int JudgmentSemanticV3VersionNumber = 3;

    private static readonly string[] JudgmentPromptKeys =
        [JudgmentSystemKey, JudgmentUserContextKey, JudgmentUserQueryKey];

    private static readonly string[] JudgmentFullPromptKeys =
        [JudgmentFullSystemKey, JudgmentFullUserContextKey, JudgmentFullUserQueryKey];

    private static readonly Dictionary<string, Func<string>> JudgmentPromptTextByKey =
        new(StringComparer.Ordinal)
        {
            [JudgmentSystemKey] = () => NdRegulPromptDefaults.JudgmentSystemPrompt.Trim(),
            [JudgmentUserContextKey] = () => NdRegulPromptDefaults.JudgmentUserContextTemplate.Trim(),
            [JudgmentUserQueryKey] = () => NdRegulPromptDefaults.JudgmentUserQueryTemplate.Trim(),
            [JudgmentFullSystemKey] = () => NdRegulPromptDefaults.JudgmentFullMarkdownSystemPrompt.Trim(),
            [JudgmentFullUserContextKey] = () => NdRegulPromptDefaults.JudgmentFullMarkdownUserContextTemplate.Trim(),
            [JudgmentFullUserQueryKey] = () => NdRegulPromptDefaults.JudgmentFullMarkdownUserQueryTemplate.Trim(),
        };

    public record PromptVersionInfo(string PromptKey, Guid Id, int VersionNumber, string Label);

    public static bool IsJudgmentPromptKey(string promptKey) =>
        JudgmentPromptKeys.Contains(promptKey, StringComparer.Ordinal)
        || JudgmentFullPromptKeys.Contains(promptKey, StringComparer.Ordinal);

    public static bool IsFullMarkdownJudgmentPromptKey(string promptKey) =>
        JudgmentFullPromptKeys.Contains(promptKey, StringComparer.Ordinal);

    private static string[] PromptKeysForWorkflow(string? workflowEngine) =>
        AnalysisWorkflowEngine.IsRegulPipelineFull(workflowEngine)
            ? JudgmentFullPromptKeys
            : JudgmentPromptKeys;

    public async Task<IReadOnlyList<PromptVersionInfo>> GetJudgmentPromptVersionsAsync(
        string? workflowEngine = null,
        CancellationToken ct = default)
    {
        await EnsureSeededAsync(ct);
        var keys = PromptKeysForWorkflow(workflowEngine);
        var rows = await db.NdAnalysisPromptVersions.AsNoTracking()
            .Where(v => keys.Contains(v.PromptKey) && v.IsCurrent)
            .ToListAsync(ct);

        return keys
            .Select(key =>
            {
                var row = rows.FirstOrDefault(r => r.PromptKey == key)
                    ?? throw new InvalidOperationException(
                        $"No current version set for prompt '{key}'. Set a current version in Admin → Analysis prompts.");
                return new PromptVersionInfo(key, row.Id, row.VersionNumber, row.Label);
            })
            .ToList();
    }

    public async Task EnsureSeededAsync(CancellationToken ct = default)
    {
        var changed = false;
        foreach (var def in NdAnalysisPromptCatalog.AllPrompts)
        {
            var exists = await db.NdAnalysisPromptVersions.AsNoTracking()
                .AnyAsync(v => v.PromptKey == def.Key, ct);
            if (exists) continue;

            db.NdAnalysisPromptVersions.Add(new NdAnalysisPromptVersion
            {
                PromptKey = def.Key,
                VersionNumber = 1,
                Label = "Base",
                PromptText = def.Text,
                IsCurrent = true,
            });
            changed = true;
        }

        if (changed)
            await db.SaveChangesAsync(ct);

        await EnsureJudgmentSemanticV2Async(ct);
        await EnsureJudgmentSemanticV3Async(ct);
        await EnsureJudgmentFullMarkdownV1Async(ct);
    }

    /// <summary>
    /// Creates judgment prompt v2 (semantic matching) and sets it current when missing.
    /// Safe to call on every startup — skips keys that already have v2.
    /// </summary>
    public async Task EnsureJudgmentSemanticV2Async(CancellationToken ct = default)
    {
        var changed = false;
        foreach (var key in JudgmentPromptKeys)
        {
            var hasV2 = await db.NdAnalysisPromptVersions.AsNoTracking()
                .AnyAsync(v => v.PromptKey == key && v.VersionNumber >= JudgmentSemanticV2VersionNumber, ct);
            if (hasV2) continue;

            if (!JudgmentPromptTextByKey.TryGetValue(key, out var textFactory))
                continue;

            var text = textFactory();
            ValidatePromptText(key, text);

            var siblings = await db.NdAnalysisPromptVersions
                .Where(v => v.PromptKey == key)
                .ToListAsync(ct);

            foreach (var sibling in siblings)
                sibling.IsCurrent = false;

            db.NdAnalysisPromptVersions.Add(new NdAnalysisPromptVersion
            {
                PromptKey = key,
                VersionNumber = JudgmentSemanticV2VersionNumber,
                Label = NdRegulPromptDefaults.JudgmentSemanticV2Label,
                PromptText = text,
                IsCurrent = true,
            });
            changed = true;
        }

        if (changed)
            await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Creates judgment prompt v3 (domain-agnostic semantic matching) and sets it current when missing.
    /// Safe to call on every startup — skips keys that already have v3.
    /// </summary>
    public async Task EnsureJudgmentSemanticV3Async(CancellationToken ct = default)
    {
        var changed = false;
        foreach (var key in JudgmentPromptKeys)
        {
            var hasV3 = await db.NdAnalysisPromptVersions.AsNoTracking()
                .AnyAsync(v => v.PromptKey == key && v.VersionNumber >= JudgmentSemanticV3VersionNumber, ct);
            if (hasV3) continue;

            if (!JudgmentPromptTextByKey.TryGetValue(key, out var textFactory))
                continue;

            var text = textFactory();
            ValidatePromptText(key, text);

            var siblings = await db.NdAnalysisPromptVersions
                .Where(v => v.PromptKey == key)
                .ToListAsync(ct);

            foreach (var sibling in siblings)
                sibling.IsCurrent = false;

            db.NdAnalysisPromptVersions.Add(new NdAnalysisPromptVersion
            {
                PromptKey = key,
                VersionNumber = JudgmentSemanticV3VersionNumber,
                Label = NdRegulPromptDefaults.JudgmentSemanticV3Label,
                PromptText = text,
                IsCurrent = true,
            });
            changed = true;
        }

        if (changed)
            await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Seeds V4-only judgment prompts (full markdown) when missing.
    /// </summary>
    public async Task EnsureJudgmentFullMarkdownV1Async(CancellationToken ct = default)
    {
        var changed = false;
        foreach (var key in JudgmentFullPromptKeys)
        {
            var exists = await db.NdAnalysisPromptVersions.AsNoTracking()
                .AnyAsync(v => v.PromptKey == key, ct);
            if (exists) continue;

            if (!JudgmentPromptTextByKey.TryGetValue(key, out var textFactory))
                continue;

            var text = textFactory();
            ValidatePromptText(key, text);

            db.NdAnalysisPromptVersions.Add(new NdAnalysisPromptVersion
            {
                PromptKey = key,
                VersionNumber = 1,
                Label = key == JudgmentFullUserContextKey
                    ? NdRegulPromptDefaults.JudgmentFullMarkdownV1Label
                    : "Base",
                PromptText = text,
                IsCurrent = true,
            });
            changed = true;
        }

        if (changed)
            await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<NdAnalysisPromptVersion>> GetVersionsAsync(string promptKey, CancellationToken ct = default)
    {
        await EnsureSeededAsync(ct);
        return await db.NdAnalysisPromptVersions.AsNoTracking()
            .Where(v => v.PromptKey == promptKey)
            .OrderByDescending(v => v.VersionNumber)
            .ToListAsync(ct);
    }

    public async Task<string> GetCurrentTextAsync(string promptKey, CancellationToken ct = default)
    {
        await EnsureSeededAsync(ct);
        var row = await db.NdAnalysisPromptVersions.AsNoTracking()
            .FirstOrDefaultAsync(v => v.PromptKey == promptKey && v.IsCurrent, ct);
        if (row != null) return row.PromptText;

        if (IsJudgmentPromptKey(promptKey))
        {
            throw new InvalidOperationException(
                $"No current version set for prompt '{promptKey}'. Set a current version in Admin → Analysis prompts.");
        }

        return NdAnalysisPromptCatalog.Find(promptKey)?.Text ?? "";
    }

    public async Task<string> GetJudgmentSystemPromptAsync(
        string? workflowEngine = null,
        CancellationToken ct = default) =>
        (await GetCurrentTextAsync(
            AnalysisWorkflowEngine.IsRegulPipelineFull(workflowEngine)
                ? JudgmentFullSystemKey
                : JudgmentSystemKey,
            ct)).Trim();

    public async Task<string> BuildJudgmentContextAsync(
        string policyContext,
        string? workflowEngine = null,
        CancellationToken ct = default)
    {
        var key = AnalysisWorkflowEngine.IsRegulPipelineFull(workflowEngine)
            ? JudgmentFullUserContextKey
            : JudgmentUserContextKey;
        var template = await GetCurrentTextAsync(key, ct);
        ValidatePromptText(key, template);
        return template.Replace("{policy_context}", policyContext, StringComparison.Ordinal);
    }

    public async Task<string> BuildJudgmentQueryAsync(
        string clauseNo,
        string clauseText,
        string? workflowEngine = null,
        CancellationToken ct = default)
    {
        var key = AnalysisWorkflowEngine.IsRegulPipelineFull(workflowEngine)
            ? JudgmentFullUserQueryKey
            : JudgmentUserQueryKey;
        var template = await GetCurrentTextAsync(key, ct);
        ValidatePromptText(key, template);
        return template
            .Replace("{clause_no}", clauseNo, StringComparison.Ordinal)
            .Replace("{clause_text}", clauseText, StringComparison.Ordinal);
    }

    public static void ValidatePromptText(string promptKey, string text)
    {
        switch (promptKey)
        {
            case JudgmentUserContextKey:
            case JudgmentFullUserContextKey:
                if (!text.Contains("{policy_context}", StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "User block 1 must include {policy_context} where internal policy text is inserted.");
                break;
            case JudgmentUserQueryKey:
            case JudgmentFullUserQueryKey:
                if (!text.Contains("{clause_no}", StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "User block 2 must include {clause_no} for the regulatory clause number.");
                if (!text.Contains("{clause_text}", StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "User block 2 must include {clause_text} for the regulatory clause text.");
                break;
        }
    }

    public async Task<NdAnalysisPromptVersion> CreateVersionAsync(
        string promptKey,
        string promptText,
        Guid createdBy,
        string? label = null,
        IReadOnlyList<Guid>? appliedSuggestionIds = null,
        CancellationToken ct = default)
    {
        if (NdAnalysisPromptCatalog.Find(promptKey) == null)
            throw new InvalidOperationException("Unknown prompt key.");

        var text = promptText?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Prompt text is required.");

        ValidatePromptText(promptKey, text);

        await EnsureSeededAsync(ct);

        var maxVersion = await db.NdAnalysisPromptVersions
            .Where(v => v.PromptKey == promptKey)
            .Select(v => (int?)v.VersionNumber)
            .MaxAsync(ct) ?? 0;

        var versionNumber = maxVersion + 1;
        var row = new NdAnalysisPromptVersion
        {
            PromptKey = promptKey,
            VersionNumber = versionNumber,
            Label = string.IsNullOrWhiteSpace(label) ? $"Version {versionNumber}" : label.Trim(),
            PromptText = text,
            IsCurrent = false,
            CreatedBy = createdBy,
        };
        db.NdAnalysisPromptVersions.Add(row);
        await db.SaveChangesAsync(ct);

        if (appliedSuggestionIds is { Count: > 0 })
        {
            var suggestionRows = await db.NdAnalysisPromptSuggestions
                .Where(s => s.PromptKey == promptKey && appliedSuggestionIds.Contains(s.Id))
                .ToListAsync(ct);
            foreach (var s in suggestionRows)
                s.AppliedInVersionId = row.Id;
            await db.SaveChangesAsync(ct);
        }

        return row;
    }

    public async Task<NdAnalysisPromptVersion> SetCurrentAsync(Guid versionId, CancellationToken ct = default)
    {
        var row = await db.NdAnalysisPromptVersions.FirstOrDefaultAsync(v => v.Id == versionId, ct)
            ?? throw new InvalidOperationException("Version not found.");

        ValidatePromptText(row.PromptKey, row.PromptText);

        var siblings = await db.NdAnalysisPromptVersions
            .Where(v => v.PromptKey == row.PromptKey)
            .ToListAsync(ct);

        foreach (var sibling in siblings)
            sibling.IsCurrent = sibling.Id == versionId;

        await db.SaveChangesAsync(ct);
        return row;
    }
}
