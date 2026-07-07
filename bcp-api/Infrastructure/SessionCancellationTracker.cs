namespace Reguliq.Api.Infrastructure;

/// <summary>In-memory cancelled session IDs — workers check before each AI pass.</summary>
public sealed class SessionCancellationTracker
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _cancelled = new();

    public void MarkCancelled(Guid sessionId) => _cancelled[sessionId] = 0;

    public bool IsCancelled(Guid sessionId) => _cancelled.ContainsKey(sessionId);
}
