using System.Collections.Concurrent;
using Reguliq.Api.Models;

namespace Reguliq.Api.Workers;

public class LocalJobQueue(ILogger<LocalJobQueue> logger)
{
    private readonly ConcurrentQueue<DualVerifyJobMessage> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private Func<DualVerifyJobMessage, CancellationToken, Task>? _handler;
    private int _concurrency = 2;

    public void SetConcurrency(int concurrency) => _concurrency = Math.Clamp(concurrency, 1, 10);

    public void RegisterHandler(Func<DualVerifyJobMessage, CancellationToken, Task> handler) => _handler = handler;

    public void Enqueue(DualVerifyJobMessage message)
    {
        _queue.Enqueue(message);
        _signal.Release();
        logger.LogDebug("Enqueued job {PointId} for session {SessionId}", message.PointId, message.SessionId);
    }

    public async Task StartAsync(CancellationToken stoppingToken)
    {
        var workers = Enumerable.Range(0, _concurrency)
            .Select(_ => RunWorkerAsync(stoppingToken))
            .ToArray();
        await Task.WhenAll(workers);
    }

    private async Task RunWorkerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await _signal.WaitAsync(ct);
            if (!_queue.TryDequeue(out var msg) || _handler == null) continue;
            try
            {
                await _handler(msg, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Job failed for point {PointId}", msg.PointId);
            }
        }
    }
}
