using System.Collections.Concurrent;

namespace Reguliq.Api.Workers;

/// <summary>One indexing job — embed and store every section of one internal document's extraction row.</summary>
public sealed record IndexingJobMessage(Guid ExtractionId);

/// <summary>
/// In-memory background queue for pgvector indexing jobs. A direct clone of <see cref="LocalJobQueue"/>'s
/// shape rather than a reuse of it — that queue is hard-typed to <see cref="Models.DualVerifyJobMessage"/>
/// with a single registered handler, so bolting a second, unrelated job type onto it would mean either
/// the wrong message shape or turning it generic — either way touching the existing dual-verify pipeline
/// for no benefit. This keeps indexing fully isolated. Same caveat as the original: in-memory only, not
/// persisted — a queued-but-not-yet-processed job is lost on restart. Acceptable here because indexing is
/// re-triggerable any time (re-running Extract enqueues it again), never a one-shot action.
/// </summary>
public class IndexingJobQueue(ILogger<IndexingJobQueue> logger)
{
    private readonly ConcurrentQueue<IndexingJobMessage> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private Func<IndexingJobMessage, CancellationToken, Task>? _handler;
    private int _concurrency = 2;

    public void SetConcurrency(int concurrency) => _concurrency = Math.Clamp(concurrency, 1, 10);

    public void RegisterHandler(Func<IndexingJobMessage, CancellationToken, Task> handler) => _handler = handler;

    public void Enqueue(IndexingJobMessage message)
    {
        _queue.Enqueue(message);
        _signal.Release();
        logger.LogDebug("Enqueued indexing job for extraction {ExtractionId}", message.ExtractionId);
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
                logger.LogError(ex, "Indexing job failed for extraction {ExtractionId}", msg.ExtractionId);
            }
        }
    }
}
