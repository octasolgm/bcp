using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Reguliq.Api.Models;

namespace Reguliq.Api.Infrastructure;

public delegate Task KafkaJobHandler(DualVerifyJobMessage message, CancellationToken ct);

/// <summary>Kafka producer + jobs-topic consumer (Azure Event Hubs SASL).</summary>
public class KafkaProducerService(
    KafkaConfig kafkaConfig,
    ILogger<KafkaProducerService> logger) : IAsyncDisposable
{
    private IProducer<string, string>? _producer;
    private IConsumer<string, string>? _consumer;
    private CancellationTokenSource? _consumerCts;
    private Task? _consumerTask;
    private KafkaJobHandler? _handler;

    public bool IsReady => _producer != null;

    public string GetTransport() => kafkaConfig.GetTransportMode();

    public async Task ConnectProducerAsync(CancellationToken ct = default)
    {
        if (!kafkaConfig.IsEnabled()) return;
        if (_producer != null) return;

        var config = BuildClientConfig(kafkaConfig.GetProducerPassword());
        _producer = new ProducerBuilder<string, string>(config).Build();
        logger.LogInformation("Kafka producer connected ({Brokers})", string.Join(", ", kafkaConfig.GetBrokers()));
        await Task.CompletedTask;
    }

    public async Task PublishJobAsync(DualVerifyJobMessage message, CancellationToken ct = default)
    {
        if (_producer == null)
            throw new InvalidOperationException("Kafka producer not connected");

        var topic = kafkaConfig.GetTopicJobs();
        var key = $"{message.SessionId}:{message.PointId}";
        var json = JsonSerializer.Serialize(message, JsonOptions);

        await _producer.ProduceAsync(topic, new Message<string, string> { Key = key, Value = json }, ct);
        logger.LogDebug("Published job {JobId} → {Topic} ({Key})", message.JobId, topic, key);
    }

    public async Task PublishToTopicAsync(
        string topicKind,
        object payload,
        CancellationToken ct = default)
    {
        if (_producer == null) return;

        var topicName = topicKind switch
        {
            "retry" => kafkaConfig.GetTopicRetry(),
            "dlq" => kafkaConfig.GetTopicDlq(),
            "results" => kafkaConfig.GetTopicResults(),
            _ => topicKind
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        string? key = null;
        if (payload is DualVerifyJobMessage job)
            key = $"{job.SessionId}:{job.PointId}";
        else if (payload is JsonElement el && el.TryGetProperty("sessionId", out var sid) && el.TryGetProperty("pointId", out var pid))
            key = $"{sid.GetString()}:{pid.GetString()}";

        var useWorkerSend = (topicKind is "retry" or "dlq")
            && kafkaConfig.GetWorkerSendPassword() != kafkaConfig.GetProducerPassword();

        if (useWorkerSend)
        {
            using var temp = new ProducerBuilder<string, string>(
                BuildClientConfig(kafkaConfig.GetWorkerSendPassword())).Build();
            await temp.ProduceAsync(topicName, new Message<string, string> { Key = key, Value = json }, ct);
            return;
        }

        await _producer.ProduceAsync(topicName, new Message<string, string> { Key = key, Value = json }, ct);
    }

    public async Task StartConsumerAsync(KafkaJobHandler handler, CancellationToken ct = default)
    {
        if (!kafkaConfig.IsEnabled()) return;
        _handler = handler;

        if (_consumer != null)
        {
            _handler = handler;
            return;
        }

        var config = BuildClientConfig(kafkaConfig.GetConsumerPassword());
        var consumerConfig = new ConsumerConfig(config)
        {
            GroupId = kafkaConfig.GetConsumerGroup(),
            AutoOffsetReset = AutoOffsetReset.Latest,
            EnableAutoCommit = false,
            SessionTimeoutMs = 30_000,
            MaxPollIntervalMs = 300_000
        };

        _consumer = new ConsumerBuilder<string, string>(consumerConfig).Build();
        _consumer.Subscribe(kafkaConfig.GetTopicJobs());
        _consumerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _consumerTask = Task.Run(() => ConsumeLoopAsync(_consumerCts.Token), _consumerCts.Token);

        logger.LogInformation(
            "Kafka consumer started (group={Group}, topic={Topic})",
            kafkaConfig.GetConsumerGroup(),
            kafkaConfig.GetTopicJobs());
        await Task.CompletedTask;
    }

    private async Task ConsumeLoopAsync(CancellationToken ct)
    {
        if (_consumer == null || _handler == null) return;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var cr = _consumer.Consume(ct);
                if (cr?.Message?.Value == null) continue;
                if (cr.Message.Key == "health-check") { _consumer.Commit(cr); continue; }

                DualVerifyJobMessage? job;
                try
                {
                    job = JsonSerializer.Deserialize<DualVerifyJobMessage>(cr.Message.Value, JsonOptions);
                }
                catch
                {
                    logger.LogError("Invalid Kafka message JSON — skipping");
                    _consumer.Commit(cr);
                    continue;
                }

                if (job == null) { _consumer.Commit(cr); continue; }

                await _handler(job, ct);
                _consumer.Commit(cr);
            }
            catch (ConsumeException ex) when (ex.Error.IsFatal)
            {
                logger.LogError(ex, "Kafka consumer fatal error");
                break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Kafka consumer loop error");
                await Task.Delay(1000, ct);
            }
        }
    }

    private ClientConfig BuildClientConfig(string password) => new()
    {
        BootstrapServers = string.Join(",", kafkaConfig.GetBrokers()),
        ClientId = kafkaConfig.GetClientId(),
        SecurityProtocol = SecurityProtocol.SaslSsl,
        SaslMechanism = SaslMechanism.Plain,
        SaslUsername = "$ConnectionString",
        SaslPassword = password
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public async ValueTask DisposeAsync()
    {
        _consumerCts?.Cancel();
        if (_consumerTask != null)
        {
            try { await _consumerTask; }
            catch (OperationCanceledException) { /* expected */ }
        }
        _consumer?.Close();
        _consumer?.Dispose();
        if (_producer != null)
        {
            _producer.Flush(TimeSpan.FromSeconds(3));
            _producer.Dispose();
        }
    }
}
