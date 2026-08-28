namespace Reguliq.Api.Infrastructure.NewDashboard;

/// <summary>
/// Tracks in-flight ND analysis runs so Stop can cancel the background processor
/// between points (and abort the current AI call when the token is observed).
/// </summary>
public sealed class NdAnalysisRunCancellationTracker
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, CancellationTokenSource> _sources = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _stopped = new();

    public CancellationToken Register(Guid runId)
    {
        _stopped.TryRemove(runId, out _);
        var cts = new CancellationTokenSource();
        if (_sources.TryRemove(runId, out var prior))
        {
            try { prior.Cancel(); } catch { /* ignore */ }
            prior.Dispose();
        }
        _sources[runId] = cts;
        return cts.Token;
    }

    /// <summary>
    /// Atomically claims the worker slot for a run — unlike <see cref="Register"/>, this fails
    /// instead of silently cancelling a prior token, so two near-simultaneous start requests for
    /// the same run can't both spawn a simulation worker (the loser's request should report the
    /// run as already in progress rather than proceeding).
    /// </summary>
    public bool TryRegister(Guid runId, out CancellationToken token)
    {
        _stopped.TryRemove(runId, out _);
        var cts = new CancellationTokenSource();
        if (!_sources.TryAdd(runId, cts))
        {
            cts.Dispose();
            token = CancellationToken.None;
            return false;
        }
        token = cts.Token;
        return true;
    }

    /// <summary>Marks a run stopped and cancels the in-flight token when present.</summary>
    public bool RequestStop(Guid runId)
    {
        _stopped[runId] = 0;
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
        _stopped.ContainsKey(runId)
        || (_sources.TryGetValue(runId, out var cts) && cts.IsCancellationRequested);

    public bool HasActiveWorker(Guid runId) => _sources.ContainsKey(runId);

    public int ActiveWorkerCount => _sources.Count;

    public IReadOnlyList<Guid> ActiveRunIds => _sources.Keys.ToArray();

    public void Clear(Guid runId)
    {
        _stopped.TryRemove(runId, out _);
        if (_sources.TryRemove(runId, out var cts))
            cts.Dispose();
    }
}
