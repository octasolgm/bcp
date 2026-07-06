import { Injectable } from '@nestjs/common';
import { ConfigService } from '@nestjs/config';

export type KafkaTransportMode = 'kafka' | 'local';

@Injectable()
export class KafkaConfigService {
  constructor(private readonly config: ConfigService) {}

  isEnabled(): boolean {
    const flag = this.config.get<string>('KAFKA_ENABLED');
    if (flag === 'false' || flag === '0') return false;
    return this.isKafkaConfigured();
  }

  isKafkaConfigured(): boolean {
    const brokers = this.config.get<string>('KAFKA_BROKERS');
    const password =
      this.config.get<string>('KAFKA_PRODUCER_CONNECTION_STRING') ??
      this.config.get<string>('KAFKA_SASL_PASSWORD');
    return Boolean(brokers?.trim() && password?.trim());
  }

  getTransportMode(): KafkaTransportMode {
    return this.isEnabled() ? 'kafka' : 'local';
  }

  getBrokers(): string[] {
    const raw = this.config.get<string>('KAFKA_BROKERS') ?? '';
    return raw
      .split(',')
      .map((b) => b.trim())
      .filter(Boolean);
  }

  getProducerPassword(): string {
    return (
      this.config.get<string>('KAFKA_PRODUCER_CONNECTION_STRING') ??
      this.config.get<string>('KAFKA_SASL_PASSWORD') ??
      ''
    );
  }

  getWorkerSendPassword(): string {
    return (
      this.config.get<string>('KAFKA_WORKER_SEND_CONNECTION_STRING') ??
      this.getProducerPassword()
    );
  }

  getConsumerPassword(): string {
    return (
      this.config.get<string>('KAFKA_CONSUMER_CONNECTION_STRING') ??
      this.config.get<string>('KAFKA_SASL_PASSWORD') ??
      this.getProducerPassword()
    );
  }

  getClientId(): string {
    return this.config.get<string>('KAFKA_CLIENT_ID') ?? 'bcp-dual-verify';
  }

  getConsumerGroup(): string {
    return (
      this.config.get<string>('KAFKA_CONSUMER_GROUP') ?? 'dual-verify-workers-v1'
    );
  }

  getTopicJobs(): string {
    return this.config.get<string>('KAFKA_TOPIC_JOBS') ?? 'dual-verify-jobs';
  }

  getTopicRetry(): string {
    return this.config.get<string>('KAFKA_TOPIC_RETRY') ?? 'dual-verify-retry';
  }

  getTopicDlq(): string {
    return this.config.get<string>('KAFKA_TOPIC_DLQ') ?? 'dual-verify-dlq';
  }

  getTopicResults(): string {
    return (
      this.config.get<string>('KAFKA_TOPIC_RESULTS') ?? 'dual-verify-results'
    );
  }

  getMaxAttempts(): number {
    const raw = this.config.get<string>('DUAL_VERIFY_MAX_RETRIES');
    const n = raw ? Number(raw) : 3;
    return Number.isFinite(n) && n > 0 ? n : 3;
  }

  getWorkerConcurrency(): number {
    const raw = this.config.get<string>('DUAL_VERIFY_WORKER_CONCURRENCY');
    const n = raw ? Number(raw) : 2;
    return Number.isFinite(n) && n > 0 ? Math.min(n, 10) : 2;
  }
}
