using System.Collections.Concurrent;

namespace Reguliq.Api.Services;

/// <summary>In-memory per-point pipeline stage for live UI polling (cleared on complete/fail).</summary>
public sealed class DualVerifyJobStageTracker
{
    private readonly ConcurrentDictionary<string, string> _stages = new();

    private static string Key(Guid sessionId, string pointId) => $"{sessionId:N}:{pointId}";

    public void Set(Guid sessionId, string pointId, string stage) =>
        _stages[Key(sessionId, pointId)] = stage;

    public bool TryGet(Guid sessionId, string pointId, out string stage) =>
        _stages.TryGetValue(Key(sessionId, pointId), out stage!);

    public void Clear(Guid sessionId, string pointId) =>
        _stages.TryRemove(Key(sessionId, pointId), out _);
}
