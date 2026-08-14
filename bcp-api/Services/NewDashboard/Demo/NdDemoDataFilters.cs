using Reguliq.Api.Data.Entities;
using Reguliq.Api.Data.NewDashboard.Entities;

namespace Reguliq.Api.Services.NewDashboard.Demo;

public static class NdDemoDataFilters
{
    public static bool IsDemoOwned(Guid? profileId, NdDemoIsolationContext ctx) =>
        profileId is Guid id && ctx.DemoProfileIds.Contains(id);

    public static bool CanAccessCreatedBy(Guid? createdBy, NdDemoIsolationContext ctx)
    {
        if (!ctx.Enabled) return true;
        if (ctx.ViewerIsDemo && createdBy == ctx.User.UserId) return true;
        var demoOwned = IsDemoOwned(createdBy, ctx);
        return ctx.ViewerIsDemo ? demoOwned : !demoOwned;
    }

    /// <summary>Demo simulation may write to this regulation row (never production-owned or template docs).</summary>
    public static bool CanDemoMutateRegulationDocument(
        NdRegulationDocument regDoc,
        NdDemoIsolationContext ctx,
        NdDemoIsolationOptions options)
    {
        if (!ctx.Enabled) return false;
        if (regDoc.Id == options.DemoRegulationTemplateDocumentId
            || regDoc.Id == options.DemoTfsRegulationTemplateDocumentId
            || regDoc.StoredDocumentId == options.DemoRegulationTemplateDocumentId
            || regDoc.StoredDocumentId == options.DemoTfsRegulationTemplateDocumentId)
            return false;
        return IsDemoOwned(regDoc.CreatedBy, ctx)
            && NdDemoIsolationHelper.ShouldSimulateAi(ctx, regDoc.CreatedBy);
    }

    public static bool CanAccessProfileEmail(string? email, NdDemoIsolationContext ctx)
    {
        if (!ctx.Enabled) return true;
        var isDemo = NdDemoIsolationHelper.IsDemoEmail(email);
        return ctx.ViewerIsDemo ? isDemo : !isDemo;
    }

    public static IQueryable<NdAnalysisPromptSuggestion> ApplyToPromptSuggestions(
        IQueryable<NdAnalysisPromptSuggestion> query,
        NdDemoIsolationContext ctx)
    {
        if (!ctx.Enabled) return query;
        if (ctx.ViewerIsDemo)
            return query.Where(s =>
                s.CreatedBy == null || ctx.DemoProfileIds.Contains(s.CreatedBy.Value));
        return query.Where(s => s.CreatedBy == null || !ctx.DemoProfileIds.Contains(s.CreatedBy.Value));
    }

    public static IQueryable<NdAnalysisPromptVersion> ApplyToPromptVersions(
        IQueryable<NdAnalysisPromptVersion> query,
        NdDemoIsolationContext ctx)
    {
        if (!ctx.Enabled) return query;
        if (ctx.ViewerIsDemo)
            return query.Where(v =>
                v.CreatedBy == null || ctx.DemoProfileIds.Contains(v.CreatedBy.Value));
        return query.Where(v => v.CreatedBy == null || !ctx.DemoProfileIds.Contains(v.CreatedBy.Value));
    }

    public static IQueryable<StoredDocument> ApplyToStoredDocuments(
        IQueryable<StoredDocument> query,
        NdDemoIsolationContext ctx)
    {
        if (!ctx.Enabled) return query;
        if (ctx.ViewerIsDemo)
            return query.Where(d => d.UploadedBy != null && ctx.DemoProfileIds.Contains(d.UploadedBy.Value));
        return query.Where(d => d.UploadedBy == null || !ctx.DemoProfileIds.Contains(d.UploadedBy.Value));
    }

    /// <summary>
    /// Markers stamped on every simulated run. Demo seeding may keep a caller-supplied run name
    /// (so the "[Demo]" prefix is not guaranteed), but the description always records that no AI
    /// credits were used — matching on both is what lets Admin → Demo clear find every demo run.
    /// </summary>
    public const string DemoRunNamePrefix = "[Demo]";

    private const string DemoRunSeedMarker = "Arena judgments seeded";
    private const string DemoRunCreditMarker = "no AI credits";

    public static bool IsDemoMarkedAnalysisRun(NdAnalysisRun run) =>
        run.Name.StartsWith(DemoRunNamePrefix, StringComparison.OrdinalIgnoreCase)
        || (run.Description?.Contains(DemoRunSeedMarker, StringComparison.OrdinalIgnoreCase) == true)
        || (run.Description?.Contains(DemoRunCreditMarker, StringComparison.OrdinalIgnoreCase) == true);

    public static IQueryable<NdAnalysisRun> ApplyToAnalysisRuns(
        IQueryable<NdAnalysisRun> query,
        NdDemoIsolationContext ctx)
    {
        if (!ctx.Enabled) return query;
        if (ctx.ViewerIsDemo)
        {
            var viewerId = ctx.User.UserId;
            return query.Where(r =>
                r.CreatedBy != null
                && (r.CreatedBy == viewerId || ctx.DemoProfileIds.Contains(r.CreatedBy.Value)));
        }
        return query.Where(r =>
            (r.CreatedBy == null || !ctx.DemoProfileIds.Contains(r.CreatedBy.Value))
            && !r.Name.StartsWith(DemoRunNamePrefix)
            && (r.Description == null
                || (!r.Description.Contains(DemoRunSeedMarker)
                    && !r.Description.Contains(DemoRunCreditMarker))));
    }

    public static IQueryable<NdLibrary> ApplyToLibraries(
        IQueryable<NdLibrary> query,
        NdDemoIsolationContext ctx)
    {
        if (!ctx.Enabled) return query;
        if (ctx.ViewerIsDemo)
            return query.Where(l => l.CreatedBy != null && ctx.DemoProfileIds.Contains(l.CreatedBy.Value));
        return query.Where(l => l.CreatedBy == null || !ctx.DemoProfileIds.Contains(l.CreatedBy.Value));
    }

    public static IQueryable<NdDepartment> ApplyToDepartments(
        IQueryable<NdDepartment> query,
        NdDemoIsolationContext ctx)
    {
        if (!ctx.Enabled) return query;
        if (ctx.ViewerIsDemo)
            return query.Where(d => d.CreatedBy != null && ctx.DemoProfileIds.Contains(d.CreatedBy.Value));
        return query.Where(d => d.CreatedBy == null || !ctx.DemoProfileIds.Contains(d.CreatedBy.Value));
    }

    public static IQueryable<NdRegulationDocument> ApplyToRegulationDocuments(
        IQueryable<NdRegulationDocument> query,
        NdDemoIsolationContext ctx)
    {
        if (!ctx.Enabled) return query;
        if (ctx.ViewerIsDemo)
            return query.Where(d =>
                d.IsManual
                || (d.CreatedBy != null && ctx.DemoProfileIds.Contains(d.CreatedBy.Value)));
        return query.Where(d =>
            d.IsManual
            || d.CreatedBy == null
            || !ctx.DemoProfileIds.Contains(d.CreatedBy.Value));
    }

    /// <summary>Department overlay rows (dept assignment on a stored doc) are not list cards.</summary>
    public static bool IsRegulationDepartmentOverlay(NdRegulationDocument d) =>
        d.StoredDocumentId.HasValue && string.IsNullOrWhiteSpace(d.FilePath);

    public static List<StoredDocument> FilterStoredDocuments(
        IEnumerable<StoredDocument> docs,
        NdDemoIsolationContext ctx) =>
        docs.Where(d => CanAccessCreatedBy(d.UploadedBy, ctx)).ToList();

    public static List<NdRegulationDocument> FilterRegulationDocuments(
        IEnumerable<NdRegulationDocument> docs,
        NdDemoIsolationContext ctx) =>
        docs.Where(d => CanAccessCreatedBy(d.CreatedBy, ctx)).ToList();

    public static List<NdProfile> FilterProfiles(
        IEnumerable<NdProfile> profiles,
        NdDemoIsolationContext ctx,
        IReadOnlyDictionary<Guid, string?> emailsById)
    {
        if (!ctx.Enabled) return profiles.ToList();
        return profiles
            .Where(p =>
            {
                var email = emailsById.TryGetValue(p.Id, out var e) ? e : null;
                return CanAccessProfileEmail(email, ctx);
            })
            .ToList();
    }

    /// <summary>User management: production admins see all users (provision demo accounts); demo admins see demo only.</summary>
    public static List<NdProfile> FilterProfilesForUserManagement(
        IEnumerable<NdProfile> profiles,
        NdDemoIsolationContext ctx,
        IReadOnlyDictionary<Guid, string?> emailsById)
    {
        if (!ctx.Enabled) return profiles.ToList();
        if (!ctx.ViewerIsDemo) return profiles.ToList();
        return FilterProfiles(profiles, ctx, emailsById);
    }
}
