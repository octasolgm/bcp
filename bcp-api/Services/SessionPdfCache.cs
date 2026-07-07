using System.Collections.Concurrent;

namespace Reguliq.Api.Services;

public class SessionPdfCache
{
    private readonly ConcurrentDictionary<Guid, byte[]> _buffers = new();

    public void Set(Guid sessionId, byte[] pdf) => _buffers[sessionId] = pdf;

    public byte[]? Get(Guid sessionId) =>
        _buffers.TryGetValue(sessionId, out var b) ? b : null;

    public void Remove(Guid sessionId) => _buffers.TryRemove(sessionId, out _);
}
