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
    public const int JudgmentSemanticV4VersionNumber = 4;
    public const int JudgmentSemanticV5VersionNumber = 5;
    public const int JudgmentSemanticV6VersionNumber = 6;
    public const int JudgmentSemanticV7VersionNumber = 7;
    public const int JudgmentSemanticV8VersionNumber = 8;
    public const int JudgmentSemanticV9VersionNumber = 9;
    public const int JudgmentSemanticV10VersionNumber = 10;
    public const int JudgmentSemanticV11VersionNumber = 11;
    public const int JudgmentFullMarkdownV2VersionNumber = 2;

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

    // Seeding is insert-only and prompt versions are platform-wide, so once it has completed in this
    // process there is nothing left to do. Without this, every prompt read (three per clause judgment)
    // re-ran ~30 existence queries against the database.
    private static volatile bool _seededThisProcess;

    public async Task EnsureSeededAsync(CancellationToken ct = default)
    {
        // Only for the real (relational) database; tests use a fresh in-memory store each time.
        var cacheable = db.Database.IsRelational();
        if (cacheable && _seededThisProcess) return;
        await EnsureSeededCoreAsync(ct);
        if (cacheable) _seededThisProcess = true;
    }

    private async Task EnsureSeededCoreAsync(CancellationToken ct)
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
        await EnsureJudgmentSemanticV4Async(ct);
        await EnsureJudgmentSemanticV5Async(ct);
        await EnsureJudgmentSemanticV6Async(ct);
        await EnsureJudgmentSemanticV7Async(ct);
        await EnsureJudgmentSemanticV8Async(ct);
        await EnsureJudgmentSemanticV9Async(ct);
        await EnsureJudgmentSemanticV10Async(ct);
        await EnsureJudgmentSemanticV11Async(ct);
        await EnsureJudgmentFullMarkdownV1Async(ct);
        await EnsureJudgmentFullMarkdownV2Async(ct);
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

    /// <summary>
    /// Creates V3-workflow judgment prompt v4 (v3 semantic matching + abbreviation dictionary,
    /// functional equivalence, OCR tolerance, per-element coverage list) and sets it current
    /// when missing. Safe to call on every startup — skips keys that already have v4.
    /// </summary>
    public async Task EnsureJudgmentSemanticV4Async(CancellationToken ct = default)
    {
        var textByKey = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [JudgmentSystemKey] = NdRegulPromptDefaults.JudgmentSystemPromptV4.Trim(),
            [JudgmentUserQueryKey] = NdRegulPromptDefaults.JudgmentUserQueryTemplateV4.Trim(),
        };

        var changed = false;
        foreach (var (key, text) in textByKey)
        {
            var hasV4 = await db.NdAnalysisPromptVersions.AsNoTracking()
                .AnyAsync(v => v.PromptKey == key && v.VersionNumber >= JudgmentSemanticV4VersionNumber, ct);
            if (hasV4) continue;

            ValidatePromptText(key, text);

            var siblings = await db.NdAnalysisPromptVersions
                .Where(v => v.PromptKey == key)
                .ToListAsync(ct);
            foreach (var sibling in siblings)
                sibling.IsCurrent = false;

            db.NdAnalysisPromptVersions.Add(new NdAnalysisPromptVersion
            {
                PromptKey = key,
                VersionNumber = JudgmentSemanticV4VersionNumber,
                Label = NdRegulPromptDefaults.JudgmentSemanticV4Label,
                PromptText = text,
                IsCurrent = true,
            });
            changed = true;
        }

        if (changed)
            await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Creates judgment prompt v5 (atomic gaps, institutional coverage, draft policy actions,
    /// strict clause scoping, domain-neutral analyst) and sets it current when missing.
    /// Safe to call on every startup — skips each key that already has version_number >= 5.
    /// Inserts only; does not modify or delete existing rows.
    /// </summary>
    public async Task EnsureJudgmentSemanticV5Async(CancellationToken ct = default)
    {
        var textByKey = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [JudgmentSystemKey] = NdRegulPromptDefaults.JudgmentSystemPromptV5.Trim(),
            [JudgmentUserContextKey] = NdRegulPromptDefaults.JudgmentUserContextTemplateV5.Trim(),
            [JudgmentUserQueryKey] = NdRegulPromptDefaults.JudgmentUserQueryTemplateV5.Trim(),
        };

        var changed = false;
        foreach (var (key, text) in textByKey)
        {
            var hasV5 = await db.NdAnalysisPromptVersions.AsNoTracking()
                .AnyAsync(v => v.PromptKey == key && v.VersionNumber >= JudgmentSemanticV5VersionNumber, ct);
            if (hasV5) continue;

            ValidatePromptText(key, text);

            var siblings = await db.NdAnalysisPromptVersions
                .Where(v => v.PromptKey == key)
                .ToListAsync(ct);
            foreach (var sibling in siblings)
                sibling.IsCurrent = false;

            db.NdAnalysisPromptVersions.Add(new NdAnalysisPromptVersion
            {
                PromptKey = key,
                VersionNumber = JudgmentSemanticV5VersionNumber,
                Label = NdRegulPromptDefaults.JudgmentSemanticV5Label,
                PromptText = text,
                IsCurrent = true,
            });
            changed = true;
        }

        if (changed)
            await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Creates judgment prompt v6 (multi-document evidence citation on top of v5 rules)
    /// and sets it current when missing. Safe on every startup — skips keys that already
    /// have version_number >= 6. Inserts only; does not modify or delete existing rows.
    /// </summary>
    public async Task EnsureJudgmentSemanticV6Async(CancellationToken ct = default)
    {
        var textByKey = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [JudgmentSystemKey] = NdRegulPromptDefaults.JudgmentSystemPromptV5.Trim(),
            [JudgmentUserContextKey] = NdRegulPromptDefaults.JudgmentUserContextTemplateV5.Trim(),
            [JudgmentUserQueryKey] = NdRegulPromptDefaults.JudgmentUserQueryTemplateV5.Trim(),
        };

        var changed = false;
        foreach (var (key, text) in textByKey)
        {
            var hasV6 = await db.NdAnalysisPromptVersions.AsNoTracking()
                .AnyAsync(v => v.PromptKey == key && v.VersionNumber >= JudgmentSemanticV6VersionNumber, ct);
            if (hasV6) continue;

            ValidatePromptText(key, text);

            var siblings = await db.NdAnalysisPromptVersions
                .Where(v => v.PromptKey == key)
                .ToListAsync(ct);
            foreach (var sibling in siblings)
                sibling.IsCurrent = false;

            db.NdAnalysisPromptVersions.Add(new NdAnalysisPromptVersion
            {
                PromptKey = key,
                VersionNumber = JudgmentSemanticV6VersionNumber,
                Label = NdRegulPromptDefaults.JudgmentSemanticV6Label,
                PromptText = text,
                IsCurrent = true,
            });
            changed = true;
        }

        if (changed)
            await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Creates judgment prompt v7 (v6 rules, but document_reference stays one string so models without
    /// schema enforcement stop returning an array) and sets it current when missing. Insert-only.
    /// </summary>
    public Task EnsureJudgmentSemanticV7Async(CancellationToken ct = default) =>
        EnsureJudgmentSemanticFromV5DefaultsAsync(JudgmentSemanticV7VersionNumber, NdRegulPromptDefaults.JudgmentSemanticV7Label, ct);

    /// <summary>v8: covered_elements, one gap line per missing requirement, status/gap consistency, legal
    /// definitions vs context. Insert-only; sets current when no version >= 8 exists.</summary>
    public Task EnsureJudgmentSemanticV8Async(CancellationToken ct = default) =>
        EnsureJudgmentSemanticFromV5DefaultsAsync(JudgmentSemanticV8VersionNumber, NdRegulPromptDefaults.JudgmentSemanticV8Label, ct);

    /// <summary>v9: v8 rules plus the supporting regulatory context ({clause_context}: parent, sibling and
    /// sub-clause headings). Insert-only; sets current when no version >= 9 exists.</summary>
    public Task EnsureJudgmentSemanticV9Async(CancellationToken ct = default) =>
        EnsureJudgmentSemanticVersionAsync(
            JudgmentSemanticV9VersionNumber,
            NdRegulPromptDefaults.JudgmentSemanticV9Label,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [JudgmentSystemKey] = NdRegulPromptDefaults.JudgmentSystemPromptV9.Trim(),
                [JudgmentUserContextKey] = NdRegulPromptDefaults.JudgmentUserContextTemplateV5.Trim(),
                [JudgmentUserQueryKey] = NdRegulPromptDefaults.JudgmentUserQueryTemplateV9.Trim(),
            },
            ct);

    /// <summary>v10: clause types, definition clauses covered by practice, illustrative lists as one requirement,
    /// merged gaps with clause words and materiality. Insert-only and NOT made current: an admin switches to it
    /// (Admin > Analysis prompts) when testing it, so runs keep using the current version until then.</summary>
    public Task EnsureJudgmentSemanticV10Async(CancellationToken ct = default) =>
        EnsureJudgmentSemanticVersionAsync(
            JudgmentSemanticV10VersionNumber,
            NdRegulPromptDefaults.JudgmentSemanticV10Label,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [JudgmentSystemKey] = NdRegulPromptDefaults.JudgmentSystemPromptV10.Trim(),
                [JudgmentUserContextKey] = NdRegulPromptDefaults.JudgmentUserContextTemplateV5.Trim(),
                [JudgmentUserQueryKey] = NdRegulPromptDefaults.JudgmentUserQueryTemplateV10.Trim(),
            },
            ct,
            makeCurrent: false);

    /// <summary>v11: v10 + a term the clause formally defines is covered only by its definition stated or adopted
    /// by reference (practice still covers the scope). Insert-only and NOT made current, like v10. A row still
    /// carrying the first v11 seed label (never edited by an admin) is refreshed to the current v11 text.</summary>
    public async Task EnsureJudgmentSemanticV11Async(CancellationToken ct = default)
    {
        var firstSeed = await db.NdAnalysisPromptVersions
            .Where(v => v.VersionNumber == JudgmentSemanticV11VersionNumber
                && v.Label == NdRegulPromptDefaults.JudgmentSemanticV11FirstSeedLabel)
            .ToListAsync(ct);
        foreach (var row in firstSeed)
        {
            row.Label = NdRegulPromptDefaults.JudgmentSemanticV11Label;
            row.PromptText = row.PromptKey switch
            {
                JudgmentSystemKey => NdRegulPromptDefaults.JudgmentSystemPromptV11.Trim(),
                JudgmentUserQueryKey => NdRegulPromptDefaults.JudgmentUserQueryTemplateV11.Trim(),
                _ => row.PromptText,
            };
        }

        if (firstSeed.Count > 0) await db.SaveChangesAsync(ct);
        await EnsureJudgmentSemanticV11InsertAsync(ct);
    }

    private Task EnsureJudgmentSemanticV11InsertAsync(CancellationToken ct) =>
        EnsureJudgmentSemanticVersionAsync(
            JudgmentSemanticV11VersionNumber,
            NdRegulPromptDefaults.JudgmentSemanticV11Label,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [JudgmentSystemKey] = NdRegulPromptDefaults.JudgmentSystemPromptV11.Trim(),
                [JudgmentUserContextKey] = NdRegulPromptDefaults.JudgmentUserContextTemplateV5.Trim(),
                [JudgmentUserQueryKey] = NdRegulPromptDefaults.JudgmentUserQueryTemplateV11.Trim(),
            },
            ct,
            makeCurrent: false);

    private Task EnsureJudgmentSemanticFromV5DefaultsAsync(int versionNumber, string label, CancellationToken ct) =>
        EnsureJudgmentSemanticVersionAsync(
            versionNumber,
            label,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [JudgmentSystemKey] = NdRegulPromptDefaults.JudgmentSystemPromptV5.Trim(),
                [JudgmentUserContextKey] = NdRegulPromptDefaults.JudgmentUserContextTemplateV5.Trim(),
                [JudgmentUserQueryKey] = NdRegulPromptDefaults.JudgmentUserQueryTemplateV5.Trim(),
            },
            ct);

    private async Task EnsureJudgmentSemanticVersionAsync(
        int versionNumber, string label, Dictionary<string, string> textByKey, CancellationToken ct, bool makeCurrent = true)
    {
        var changed = false;
        foreach (var (key, text) in textByKey)
        {
            var exists = await db.NdAnalysisPromptVersions.AsNoTracking()
                .AnyAsync(v => v.PromptKey == key && v.VersionNumber >= versionNumber, ct);
            if (exists) continue;

            ValidatePromptText(key, text);

            if (makeCurrent)
            {
                var siblings = await db.NdAnalysisPromptVersions
                    .Where(v => v.PromptKey == key)
                    .ToListAsync(ct);
                foreach (var sibling in siblings)
                    sibling.IsCurrent = false;
            }

            db.NdAnalysisPromptVersions.Add(new NdAnalysisPromptVersion
            {
                PromptKey = key,
                VersionNumber = versionNumber,
                Label = label,
                PromptText = text,
                IsCurrent = makeCurrent,
            });
            changed = true;
        }

        if (changed)
            await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Creates V4 judgment prompt v2 (abbreviation dictionary, functional equivalence, OCR
    /// tolerance, per-element coverage list) and sets it current when missing.
    /// Safe to call on every startup — skips keys that already have v2.
    /// </summary>
    public async Task EnsureJudgmentFullMarkdownV2Async(CancellationToken ct = default)
    {
        var textByKey = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [JudgmentFullSystemKey] = NdRegulPromptDefaults.JudgmentFullMarkdownSystemPromptV2.Trim(),
            [JudgmentFullUserQueryKey] = NdRegulPromptDefaults.JudgmentFullMarkdownUserQueryTemplateV2.Trim(),
        };

        var changed = false;
        foreach (var (key, text) in textByKey)
        {
            var hasV2 = await db.NdAnalysisPromptVersions.AsNoTracking()
                .AnyAsync(v => v.PromptKey == key && v.VersionNumber >= JudgmentFullMarkdownV2VersionNumber, ct);
            if (hasV2) continue;

            ValidatePromptText(key, text);

            var siblings = await db.NdAnalysisPromptVersions
                .Where(v => v.PromptKey == key)
                .ToListAsync(ct);
            foreach (var sibling in siblings)
                sibling.IsCurrent = false;

            db.NdAnalysisPromptVersions.Add(new NdAnalysisPromptVersion
            {
                PromptKey = key,
                VersionNumber = JudgmentFullMarkdownV2VersionNumber,
                Label = NdRegulPromptDefaults.JudgmentFullMarkdownV2Label,
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

    /// <summary>Fills the current user block 2. {clause_context} (v9+) receives the clause's supporting
    /// regulatory context; templates without the placeholder simply do not send it.</summary>
    public async Task<string> BuildJudgmentQueryAsync(
        string clauseNo,
        string clauseText,
        string? workflowEngine = null,
        CancellationToken ct = default,
        string? clauseContext = null)
    {
        var key = AnalysisWorkflowEngine.IsRegulPipelineFull(workflowEngine)
            ? JudgmentFullUserQueryKey
            : JudgmentUserQueryKey;
        var template = await GetCurrentTextAsync(key, ct);
        ValidatePromptText(key, template);
        // {clause_context} first so a clause text that happens to contain the placeholder is never touched.
        return template
            .Replace(
                "{clause_context}",
                string.IsNullOrWhiteSpace(clauseContext) ? NdRegulPromptDefaults.NoClauseContextAvailable : clauseContext,
                StringComparison.Ordinal)
            .Replace("{clause_no}", clauseNo, StringComparison.Ordinal)
            .Replace("{clause_text}", clauseText, StringComparison.Ordinal);
    }

    /// <summary>Whether the current user block 2 sends the supporting regulatory context.</summary>
    public async Task<bool> CurrentQueryUsesClauseContextAsync(string? workflowEngine = null, CancellationToken ct = default)
    {
        var key = AnalysisWorkflowEngine.IsRegulPipelineFull(workflowEngine)
            ? JudgmentFullUserQueryKey
            : JudgmentUserQueryKey;
        return (await GetCurrentTextAsync(key, ct)).Contains("{clause_context}", StringComparison.Ordinal);
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

        await SwitchCurrentAsync(
            db.NdAnalysisPromptVersions.Where(v => v.PromptKey == row.PromptKey), row, ct);
        return row;
    }

    /// <summary>
    /// Makes <paramref name="row"/> the current version among <paramref name="siblings"/>. The old current
    /// flag is cleared before the new one is set, in one transaction, because the partial unique index
    /// idx_nd_prompt_versions_current is checked per row and a single batched save can set the new flag first.
    /// </summary>
    public async Task SwitchCurrentAsync(
        IQueryable<NdAnalysisPromptVersion> siblings,
        NdAnalysisPromptVersion row,
        CancellationToken ct = default)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await siblings
                .Where(v => v.IsCurrent && v.Id != row.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(v => v.IsCurrent, false), ct);
            await db.NdAnalysisPromptVersions
                .Where(v => v.Id == row.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(v => v.IsCurrent, true), ct);
            await tx.CommitAsync(ct);
        });

        row.IsCurrent = true;
        db.Entry(row).Property(v => v.IsCurrent).IsModified = false;
    }
}
