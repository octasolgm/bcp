using Reguliq.Api.Infrastructure;
using Reguliq.Api.Models;
using Reguliq.Api.Workers;

namespace Reguliq.Api.Workers;

/// <summary>Starts local queue and/or Kafka consumer for dual-verify jobs.</summary>
public class DualVerifyWorkerHosted(
    DualVerifyJobProcessor processor,
    LocalJobQueue localQueue,
    KafkaConfig kafkaConfig,
    KafkaProducerService kafka,
    ILogger<DualVerifyWorkerHosted> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        localQueue.SetConcurrency(kafkaConfig.GetWorkerConcurrency());
        KafkaJobHandler handler = (msg, ct) => processor.ProcessJobAsync(msg, ct);
        localQueue.RegisterHandler((msg, ct) => processor.ProcessJobAsync(msg, ct));

        if (kafkaConfig.IsEnabled())
        {
            try
            {
                await kafka.ConnectProducerAsync(cancellationToken);
                await kafka.StartConsumerAsync(handler, cancellationToken);
                logger.LogInformation("Dual verify worker using Kafka transport");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Kafka consumer failed — falling back to local queue only");
                _ = localQueue.StartAsync(cancellationToken);
            }
        }
        else
        {
            _ = localQueue.StartAsync(cancellationToken);
            logger.LogInformation("Dual verify worker using local in-process queue");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
