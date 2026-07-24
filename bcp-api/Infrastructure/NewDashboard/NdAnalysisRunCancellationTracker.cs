namespace Reguliq.Api.Infrastructure.NewDashboard;

/// <summary>
/// Tracks in-flight ND analysis runs so Stop can cancel the background processor
/// between points (and abort the current AI call when the token is observed).
/// </summary>
public sealed class NdAnalysisRunCancellationTracker
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, CancellationTokenSource> _sources = new();

    public CancellationToken Register(Guid runId)
    {
        var cts = new CancellationTokenSource();
        if (_sources.TryRemove(runId, out var prior))
        {
            try { prior.Cancel(); } catch { /* ignore */ }
            prior.Dispose();
        }
        _sources[runId] = cts;
        return cts.Token;
    }

    public bool RequestStop(Guid runId)
    {
        if (!_sources.TryGetValue(runId, out var cts))
            return false;
        try
        {
            if (!cts.IsCancellationRequested)
                cts.Cancel();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool IsStopRequested(Guid runId) =>
        _sources.TryGetValue(runId, out var cts) && cts.IsCancellationRequested;

    public void Clear(Guid runId)
    {
        if (_sources.TryRemove(runId, out var cts))
            cts.Dispose();
    }
}
