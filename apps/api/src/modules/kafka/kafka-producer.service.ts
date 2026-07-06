import { Injectable, Logger, OnModuleDestroy, OnModuleInit } from '@nestjs/common';
import { Kafka, logLevel, type Consumer, type Producer } from 'kafkajs';
import { KafkaConfigService } from './kafka.config';
import type { DualVerifyJobMessage } from '../dual-verify-kafka/dual-verify-kafka.types';

export type JobMessageHandler = (message: DualVerifyJobMessage) => Promise<void>;

@Injectable()
export class KafkaProducerService implements OnModuleInit, OnModuleDestroy {
  private readonly logger = new Logger(KafkaProducerService.name);
  private kafka: Kafka | null = null;
  private consumerKafka: Kafka | null = null;
  private producer: Producer | null = null;
  private consumer: Consumer | null = null;
  private handler: JobMessageHandler | null = null;
  private consuming = false;

  constructor(private readonly kafkaConfig: KafkaConfigService) {}

  async onModuleInit(): Promise<void> {
    if (!this.kafkaConfig.isEnabled()) {
      this.logger.log('Kafka disabled — using in-process local queue');
      return;
    }

    this.kafka = new Kafka({
      clientId: this.kafkaConfig.getClientId(),
      brokers: this.kafkaConfig.getBrokers(),
      ssl: true,
      sasl: {
        mechanism: 'plain',
        username: '$ConnectionString',
        password: this.kafkaConfig.getProducerPassword(),
      },
      logLevel: logLevel.WARN,
    });

    this.producer = this.kafka.producer();
    await this.producer.connect();
    this.logger.log(
      `Kafka producer connected (${this.kafkaConfig.getBrokers().join(', ')})`,
    );
  }

  async onModuleDestroy(): Promise<void> {
    this.consuming = false;
    if (this.consumer) {
      await this.consumer.disconnect().catch(() => undefined);
    }
    if (this.producer) {
      await this.producer.disconnect().catch(() => undefined);
    }
    this.consumerKafka = null;
    this.kafka = null;
  }

  getTransport(): 'kafka' | 'local' {
    return this.kafkaConfig.getTransportMode();
  }

  isKafkaReady(): boolean {
    return Boolean(this.producer);
  }

  /** Publish one dual-verify job message */
  async publishJob(message: DualVerifyJobMessage): Promise<void> {
    if (!this.producer) {
      throw new Error('Kafka producer not connected');
    }

    const topic = this.kafkaConfig.getTopicJobs();
    const key = `${message.sessionId}:${message.pointId}`;

    await this.producer.send({
      topic,
      messages: [
        {
          key,
          value: JSON.stringify(message),
          headers: {
            schemaVersion: message.schemaVersion,
            correlationId: message.correlationId,
            attempt: String(message.attempt),
          },
        },
      ],
    });

    this.logger.debug(`Published job ${message.jobId} → ${topic} (${key})`);
  }

  /** Publish to retry or DLQ topic (worker-send policy when configured) */
  async publishToTopic(
    topic: 'retry' | 'dlq' | 'results',
    message: DualVerifyJobMessage | Record<string, unknown>,
  ): Promise<void> {
    if (!this.kafka && !this.producer) return;

    const topicName =
      topic === 'retry'
        ? this.kafkaConfig.getTopicRetry()
        : topic === 'dlq'
          ? this.kafkaConfig.getTopicDlq()
          : this.kafkaConfig.getTopicResults();

    const payload =
      'sessionId' in message && 'pointId' in message
        ? {
            key: `${message.sessionId}:${message.pointId}`,
            value: JSON.stringify(message),
          }
        : { value: JSON.stringify(message) };

    const useWorkerSend =
      (topic === 'retry' || topic === 'dlq') &&
      this.kafkaConfig.getWorkerSendPassword() !==
        this.kafkaConfig.getProducerPassword();

    if (useWorkerSend && this.kafka) {
      const client = new Kafka({
        clientId: `${this.kafkaConfig.getClientId()}-worker-send`,
        brokers: this.kafkaConfig.getBrokers(),
        ssl: true,
        sasl: {
          mechanism: 'plain',
          username: '$ConnectionString',
          password: this.kafkaConfig.getWorkerSendPassword(),
        },
        logLevel: logLevel.WARN,
      });
      const tempProducer = client.producer();
      await tempProducer.connect();
      await tempProducer.send({ topic: topicName, messages: [payload] });
      await tempProducer.disconnect();
      return;
    }

    if (!this.producer) return;
    await this.producer.send({ topic: topicName, messages: [payload] });
  }

  /** Start Kafka consumer loop (listen-only connection string) */
  async startConsumer(handler: JobMessageHandler): Promise<void> {
    if (this.consuming && this.consumer) {
      this.handler = handler;
      return;
    }

    if (this.consumer) {
      await this.consumer.disconnect().catch(() => undefined);
      this.consumer = null;
      this.consuming = false;
    }

    this.handler = handler;

    this.consumerKafka = new Kafka({
      clientId: `${this.kafkaConfig.getClientId()}-consumer`,
      brokers: this.kafkaConfig.getBrokers(),
      ssl: true,
      sasl: {
        mechanism: 'plain',
        username: '$ConnectionString',
        password: this.kafkaConfig.getConsumerPassword(),
      },
      logLevel: logLevel.WARN,
    });

    this.consumer = this.consumerKafka.consumer({
      groupId: this.kafkaConfig.getConsumerGroup(),
      sessionTimeout: 30_000,
      heartbeatInterval: 3_000,
      maxWaitTimeInMs: 5_000,
    });

    await this.consumer.connect();
    await this.consumer.subscribe({
      topic: this.kafkaConfig.getTopicJobs(),
      fromBeginning: false,
    });

    this.consuming = true;
    this.logger.log(
      `Kafka consumer started (group=${this.kafkaConfig.getConsumerGroup()})`,
    );

    void this.consumer
      .run({
        autoCommit: false,
        eachMessage: async ({ message, partition, topic, heartbeat }) => {
          if (!this.handler || !message.value) return;

          this.logger.log(
            `Kafka message received partition=${partition} offset=${message.offset}`,
          );

          let job: DualVerifyJobMessage;
        try {
          job = JSON.parse(message.value.toString()) as DualVerifyJobMessage;
        } catch {
          this.logger.error('Invalid Kafka message JSON — skipping');
          if (this.consumer) {
            await this.consumer.commitOffsets([
              {
                topic,
                partition,
                offset: (BigInt(message.offset) + 1n).toString(),
              },
            ]);
          }
          return;
        }

        await heartbeat();
        await this.handler(job);

        if (this.consumer) {
          await this.consumer.commitOffsets([
            {
              topic,
              partition,
              offset: (BigInt(message.offset) + 1n).toString(),
            },
          ]);
        }
      },
    }).catch((err: unknown) => {
      this.consuming = false;
      this.logger.error(
        `Kafka consumer run loop stopped: ${err instanceof Error ? err.message : err}`,
      );
    });
  }
}
