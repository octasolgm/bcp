using Reguliq.Api.Infrastructure.NewDashboard;

namespace Reguliq.Api.Services.NewDashboard.Ai;

/// <summary>
/// Who an AI call should be billed to. Inside an ND request this follows the request's workspace; the
/// analysis pipeline runs on background tasks, so a run opens a scope of its own from the run's workspace
/// (see <see cref="Enter"/>). Without it a call is recorded against no workspace rather than the wrong one.
/// </summary>
public static class NdAiUsageContext
{
    private static readonly AsyncLocal<NdAiUsageScope?> Current = new();

    public static NdAiUsageScope? Value => Current.Value ?? FromRequestWorkspace();

    /// <summary>Bills every AI call made inside the returned scope to this workspace and run.</summary>
    public static IDisposable Enter(Guid? tenantId, Guid? analysisRunId, string feature, Guid? userId = null)
    {
        var previous = Current.Value;
        Current.Value = new NdAiUsageScope(tenantId, analysisRunId, feature, userId);
        return new Restore(previous);
    }

    private static NdAiUsageScope? FromRequestWorkspace() =>
        WorkspaceScope.CurrentWorkspaceId is Guid id ? new NdAiUsageScope(id, null, "request", null) : null;

    private sealed class Restore(NdAiUsageScope? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}

public sealed record NdAiUsageScope(Guid? TenantId, Guid? AnalysisRunId, string Feature, Guid? UserId);
