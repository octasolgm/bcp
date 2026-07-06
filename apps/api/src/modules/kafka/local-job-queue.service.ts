import { Injectable, Logger } from '@nestjs/common';
import type { DualVerifyJobMessage } from '../dual-verify-kafka/dual-verify-kafka.types';

export type LocalQueueHandler = (message: DualVerifyJobMessage) => Promise<void>;

/**
 * In-process queue when KAFKA_ENABLED=false or Azure not configured.
 * Same message contract as Kafka — swap transport without code changes.
 */
@Injectable()
export class LocalJobQueueService {
  private readonly logger = new Logger(LocalJobQueueService.name);
  private readonly queue: DualVerifyJobMessage[] = [];
  private handler: LocalQueueHandler | null = null;
  private active = 0;
  private maxConcurrency = 2;
  private draining = false;

  setConcurrency(n: number): void {
    this.maxConcurrency = Math.max(1, Math.min(n, 10));
  }

  registerHandler(handler: LocalQueueHandler): void {
    this.handler = handler;
    void this.drain();
  }

  enqueue(message: DualVerifyJobMessage): void {
    this.queue.push(message);
    this.logger.debug(
      `Local queue +1 (${message.sessionId}:${message.pointId}) depth=${this.queue.length}`,
    );
    void this.drain();
  }

  enqueueMany(messages: DualVerifyJobMessage[]): void {
    this.queue.push(...messages);
    void this.drain();
  }

  getDepth(): number {
    return this.queue.length;
  }

  private async drain(): Promise<void> {
    if (this.draining || !this.handler) return;
    this.draining = true;

    try {
      while (this.queue.length > 0 && this.active < this.maxConcurrency) {
        const job = this.queue.shift();
        if (!job) break;
        this.active += 1;
        void this.runOne(job);
      }
    } finally {
      this.draining = false;
    }
  }

  private async runOne(job: DualVerifyJobMessage): Promise<void> {
    try {
      if (this.handler) {
        await this.handler(job);
      }
    } catch (err) {
      this.logger.error(
        `Local queue job failed ${job.sessionId}:${job.pointId}: ${err}`,
      );
    } finally {
      this.active -= 1;
      void this.drain();
    }
  }
}
