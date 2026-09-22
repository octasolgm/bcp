namespace Reguliq.Api.Infrastructure.NewDashboard;

/// <summary>
/// The workspace (tenant) the current request acts in. Set once per ND request by
/// <see cref="WorkspaceResolutionMiddleware"/> and read by AppDbContext's query filters and insert stamping.
///
/// AsyncLocal rather than a scoped service so it also reaches code that opens its own DI scope or
/// DbContext inside the request (and background Task.Run work started from it). Hosted workers and
/// startup code never see a value, so they run unfiltered, which is what their by-id processing needs.
/// </summary>
public static class WorkspaceScope
{
    /// <summary>Fixed id of the workspace that owns all data created before workspaces existed (and demo).</summary>
    public static readonly Guid DefaultWorkspaceId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private static readonly AsyncLocal<WorkspaceScopeState?> Current = new();

    /// <summary>Active workspace id, or null when no request scope applies (no filtering).</summary>
    public static Guid? CurrentWorkspaceId => Current.Value?.WorkspaceId;

    public static WorkspaceScopeState? State => Current.Value;

    /// <summary>
    /// Legacy tables (document_analysis_runs, dual_verify_sessions) predate workspaces and carry no
    /// tenant, so they only surface inside the Default workspace that owns all old data.
    /// </summary>
    public static bool InDefaultWorkspace =>
        CurrentWorkspaceId is not Guid id || id == DefaultWorkspaceId;

    public static void Set(WorkspaceScopeState? state) => Current.Value = state;

    /// <summary>Runs <paramref name="action"/> with no workspace filter (platform-level queries).</summary>
    public static async Task<T> RunUnscopedAsync<T>(Func<Task<T>> action)
    {
        var previous = Current.Value;
        Current.Value = null;
        try
        {
            return await action();
        }
        finally
        {
            Current.Value = previous;
        }
    }
}

public sealed record WorkspaceScopeState(
    Guid WorkspaceId,
    Guid HomeWorkspaceId,
    bool WorkspaceIsActive,
    bool IsPlatformAdmin);
