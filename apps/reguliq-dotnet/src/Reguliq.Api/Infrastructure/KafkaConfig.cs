namespace Reguliq.Api.Infrastructure;

/// <summary>Kafka / dual-verify transport configuration (mirrors NestJS KafkaConfigService).</summary>
public class KafkaConfig(IConfiguration config)
{
    public bool IsKafkaConfigured()
    {
        var brokers = config["KAFKA_BROKERS"];
        var password = config["KAFKA_PRODUCER_CONNECTION_STRING"]
            ?? config["KAFKA_SASL_PASSWORD"];
        return !string.IsNullOrWhiteSpace(brokers) && !string.IsNullOrWhiteSpace(password);
    }

    public bool IsEnabled()
    {
        var flag = config["KAFKA_ENABLED"];
        if (flag is "false" or "0") return false;
        return IsKafkaConfigured();
    }

    public string GetTransportMode() => IsEnabled() ? "kafka" : "local";

    public string[] GetBrokers() =>
        (config["KAFKA_BROKERS"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public string GetProducerPassword() =>
        config["KAFKA_PRODUCER_CONNECTION_STRING"]
        ?? config["KAFKA_SASL_PASSWORD"]
        ?? "";

    public string GetWorkerSendPassword() =>
        config["KAFKA_WORKER_SEND_CONNECTION_STRING"] ?? GetProducerPassword();

    public string GetConsumerPassword() =>
        config["KAFKA_CONSUMER_CONNECTION_STRING"]
        ?? config["KAFKA_SASL_PASSWORD"]
        ?? GetProducerPassword();

    public string GetClientId() => config["KAFKA_CLIENT_ID"] ?? "bcp-dual-verify";

    public string GetConsumerGroup() =>
        config["KAFKA_CONSUMER_GROUP"] ?? "dual-verify-workers-v1";

    public string GetTopicJobs() => config["KAFKA_TOPIC_JOBS"] ?? "dual-verify-jobs";
    public string GetTopicRetry() => config["KAFKA_TOPIC_RETRY"] ?? "dual-verify-retry";
    public string GetTopicDlq() => config["KAFKA_TOPIC_DLQ"] ?? "dual-verify-dlq";
    public string GetTopicResults() => config["KAFKA_TOPIC_RESULTS"] ?? "dual-verify-results";

    public int GetMaxAttempts()
    {
        var n = config.GetValue("DUAL_VERIFY_MAX_RETRIES", 3);
        return n > 0 ? n : 3;
    }

    public int GetWorkerConcurrency()
    {
        var n = config.GetValue("DUAL_VERIFY_WORKER_CONCURRENCY", 2);
        return n > 0 ? Math.Min(n, 10) : 2;
    }
}
