import {
  Injectable,
  Logger,
  OnModuleInit,
  ServiceUnavailableException,
} from '@nestjs/common';
import { ConfigService } from '@nestjs/config';
import { readFileSync, existsSync } from 'fs';
import { resolve } from 'path';
import { randomUUID } from 'crypto';
import { BcpAnalyzeService } from '../ai/services/bcp-analyze.service';
import type { UploadedPdfFile } from '../ai/types/ai-response.types';
import { findBuiltinDoc } from '../landing-ai/builtin-docs';
import { LandingAiService } from '../landing-ai/services/landing-ai.service';
import type { GovRequirementPoint } from '../landing-ai/types/landing-ai.types';
import { KafkaConfigService } from '../kafka/kafka.config';
import { KafkaProducerService } from '../kafka/kafka-producer.service';
import { LocalJobQueueService } from '../kafka/local-job-queue.service';
import { DualVerifyKafkaStoreService } from './dual-verify-kafka-store.service';
import type { DualVerifyJobMessage } from './dual-verify-kafka.types';
import { compareDualVerifyResults } from './utils/dual-verify-agreement';
import { buildDualVerifyPrompt } from './utils/dual-verify-prompt';

@Injectable()
export class DualVerifyKafkaWorkerService implements OnModuleInit {
  private readonly logger = new Logger(DualVerifyKafkaWorkerService.name);

  constructor(
    private readonly landingAi: LandingAiService,
    private readonly bcpAnalyze: BcpAnalyzeService,
    private readonly store: DualVerifyKafkaStoreService,
    private readonly kafkaConfig: KafkaConfigService,
    private readonly kafkaProducer: KafkaProducerService,
    private readonly localQueue: LocalJobQueueService,
    private readonly config: ConfigService,
  ) {}

  async onModuleInit(): Promise<void> {
    this.localQueue.setConcurrency(this.kafkaConfig.getWorkerConcurrency());

    const handler = (msg: DualVerifyJobMessage) => this.processJob(msg);

    if (this.kafkaConfig.isEnabled()) {
      void this.kafkaProducer.startConsumer(handler).catch((err) => {
        this.logger.error(
          `Kafka consumer failed to start — falling back to local queue: ${err}`,
        );
        this.localQueue.registerHandler(handler);
      });
    } else {
      this.localQueue.registerHandler(handler);
      this.logger.log('Dual verify worker using local in-process queue');
    }
  }

  /** Process one dual-verify job (Phase 1 → Phase 2 → agreement → save) */
  async processJob(job: DualVerifyJobMessage): Promise<void> {
    const existing = await this.store.getPointJob(job.sessionId, job.pointId);
    if (existing?.status === 'completed') {
      this.logger.debug(`Skip duplicate completed job ${job.pointId}`);
      return;
    }
    if (existing?.status === 'running') {
      this.logger.debug(`Skip duplicate running job ${job.pointId}`);
      return;
    }

    const now = new Date().toISOString();
    const pointJobId = existing?.id ?? job.jobId;

    await this.store.savePointJob({
      id: pointJobId,
      sessionId: job.sessionId,
      pointId: job.pointId,
      pointTitle: job.pointTitle,
      govText: job.govText,
      status: 'running',
      attempt: job.attempt,
      maxAttempts: job.maxAttempts,
      startedAt: now,
      completedAt: null,
      createdAt: existing?.createdAt ?? now,
      updatedAt: now,
    });

    await this.store.updateSessionCounts(job.sessionId);

    const point: GovRequirementPoint = {
      point_id: job.pointId,
      title: job.pointTitle,
      text: job.govText,
      point_type: 'mandatory',
    };

    try {
      const phase1 = await this.runPhase1(job, point);
      const phase2 = await this.runPhase2(job, point, phase1);
      const agreement = compareDualVerifyResults(phase1, phase2);

      await this.store.savePointJob({
        id: pointJobId,
        sessionId: job.sessionId,
        pointId: job.pointId,
        pointTitle: job.pointTitle,
        govText: job.govText,
        status: 'completed',
        attempt: job.attempt,
        maxAttempts: job.maxAttempts,
        landingMessage: phase1,
        llmMessage: phase2,
        agreementJson: agreement,
        startedAt: now,
        completedAt: new Date().toISOString(),
        createdAt: existing?.createdAt ?? now,
        updatedAt: new Date().toISOString(),
      });

      await this.saveComplianceIncremental(job, phase1, phase2, agreement);
      await this.store.updateSessionCounts(job.sessionId);

      if (this.kafkaConfig.isEnabled()) {
        await this.kafkaProducer.publishToTopic('results', {
          sessionId: job.sessionId,
          pointId: job.pointId,
          status: 'completed',
          agreement: agreement.status,
          completedAt: new Date().toISOString(),
        });
      }

      this.logger.log(
        `Dual verify completed ${job.sessionId}:${job.pointId} → ${agreement.status}`,
      );
    } catch (err) {
      await this.handleFailure(job, pointJobId, existing?.createdAt ?? now, err);
    }
  }

  private async runPhase1(
    job: DualVerifyJobMessage,
    point: GovRequirementPoint,
  ): Promise<string> {
    const forceCompare = job.forceRefresh === true;
    const result = await this.landingAi.comparePoint(
      point,
      [],
      job.internalFileName,
      undefined,
      job.internalFileHash,
      forceCompare,
    );

    const message = result.message?.trim();
    if (!message) {
      throw new ServiceUnavailableException('Landing AI returned empty Phase 1 message');
    }
    if (result.cached) {
      this.logger.log(`Phase 1 cache hit ${job.pointId} (0 Landing AI credits)`);
    }
    return message;
  }

  private async runPhase2(
    job: DualVerifyJobMessage,
    point: GovRequirementPoint,
    landingMessage: string,
  ): Promise<string> {
    const markdownSupplement = await this.loadInternalMarkdown(job);
    const prompt = buildDualVerifyPrompt(point, landingMessage, markdownSupplement);
    const pdfFiles = this.resolveInternalPdfs(job);

    if (!pdfFiles.length && !markdownSupplement) {
      throw new ServiceUnavailableException(
        'Phase 2 needs internal PDF (upload in UI) or parsed markdown in Supabase. Upload PDF or run POST /landing-ai/seed/builtin.',
      );
    }

    const result = await this.bcpAnalyze.analyze(
      pdfFiles,
      prompt,
      job.phase2Model,
    );

    if (!result.success) {
      throw new ServiceUnavailableException(
        result.message ?? result.error ?? 'Gemini Phase 2 failed',
      );
    }

    const message =
      typeof result.message === 'string'
        ? result.message.trim()
        : JSON.stringify(result.message);

    if (!message) {
      throw new ServiceUnavailableException('Gemini returned empty Phase 2 message');
    }
    return message;
  }

  private resolveInternalPdfs(job: DualVerifyJobMessage): UploadedPdfFile[] {
    const files: UploadedPdfFile[] = [];

    const sessionBuffer = this.store.getInternalPdfBuffer(job.sessionId);
    if (sessionBuffer?.length) {
      files.push({
        originalname: job.internalFileName,
        buffer: sessionBuffer,
        size: sessionBuffer.length,
        mimetype: 'application/pdf',
      });
      return files;
    }

    const envPath = this.config.get<string>('DUAL_VERIFY_INTERNAL_PDF_PATH');
    if (envPath && existsSync(envPath)) {
      const buffer = readFileSync(envPath);
      files.push({
        originalname: job.internalFileName,
        buffer,
        size: buffer.length,
        mimetype: 'application/pdf',
      });
      return files;
    }

    const defaultPath = this.resolveDefaultInternalPdfPath();
    if (defaultPath) {
      const buffer = readFileSync(defaultPath);
      files.push({
        originalname: job.internalFileName,
        buffer,
        size: buffer.length,
        mimetype: 'application/pdf',
      });
    }

    return files;
  }

  /** Monorepo root default IMPTFS PDF — works regardless of process.cwd(). */
  private resolveDefaultInternalPdfPath(): string | null {
    const candidates = [
      resolve(process.cwd(), 'apps/web/public/default-docs/imptfs.pdf'),
      resolve(process.cwd(), '../web/public/default-docs/imptfs.pdf'),
      resolve(__dirname, '../../../../../web/public/default-docs/imptfs.pdf'),
      resolve(__dirname, '../../../../web/public/default-docs/imptfs.pdf'),
    ];
    return candidates.find((p) => existsSync(p)) ?? null;
  }

  /** Parsed IMPTFS markdown from Supabase — used when PDF unavailable (Phase 2 supplement) */
  private async loadInternalMarkdown(job: DualVerifyJobMessage): Promise<string | undefined> {
    try {
      const parsed = await this.landingAi.getStoredParse(job.internalFileHash);
      const md = parsed?.markdown?.trim();
      return md && md.length > 100 ? md : undefined;
    } catch {
      return undefined;
    }
  }

  private async saveComplianceIncremental(
    job: DualVerifyJobMessage,
    landingMessage: string,
    llmMessage: string,
    agreement: ReturnType<typeof compareDualVerifyResults>,
  ): Promise<void> {
    const granularity =
      job.granularity === 'leaf' ? 'dual-leaf' : 'dual-section';

    try {
      await this.landingAi.saveComplianceSession({
        govFileHash: job.govFileHash,
        internalFileHash: job.internalFileHash,
        govFileName: job.govFileName,
        internalFileName: job.internalFileName,
        totalGovPoints: 0,
        comparedPoints: 1,
        skippedPoints: 0,
        skippedJson: [],
        compareGranularity: granularity,
        summaryJson: {
          pipeline: 'kafka-dual-verify',
          sessionId: job.sessionId,
          phase2Model: job.phase2Model,
          transport: this.kafkaConfig.getTransportMode(),
        },
        resultsJson: [
          {
            point_id: job.pointId,
            title: job.pointTitle,
            text: job.govText,
            message: landingMessage,
            landingMessage,
            llmMessage,
            agreementJson: agreement,
          },
        ],
      });
    } catch (err) {
      this.logger.error(
        `Compliance session save failed (apply migration 002): ${err instanceof Error ? err.message : err}`,
      );
    }
  }

  private async handleFailure(
    job: DualVerifyJobMessage,
    pointJobId: string,
    createdAt: string,
    err: unknown,
  ): Promise<void> {
    const message = err instanceof Error ? err.message : String(err);
    const isTransient = this.isTransientError(message);
    const canRetry = isTransient && job.attempt < job.maxAttempts;

    if (canRetry) {
      const retryJob: DualVerifyJobMessage = {
        ...job,
        messageId: randomUUID(),
        attempt: job.attempt + 1,
        createdAt: new Date().toISOString(),
      };

      this.logger.warn(
        `Retry ${job.pointId} attempt ${retryJob.attempt}/${job.maxAttempts}: ${message}`,
      );

      await this.store.savePointJob({
        id: pointJobId,
        sessionId: job.sessionId,
        pointId: job.pointId,
        pointTitle: job.pointTitle,
        govText: job.govText,
        status: 'queued',
        attempt: retryJob.attempt,
        maxAttempts: job.maxAttempts,
        errorMessage: message,
        startedAt: null,
        completedAt: null,
        createdAt,
        updatedAt: new Date().toISOString(),
      });

      if (this.kafkaConfig.isEnabled()) {
        await this.kafkaProducer.publishToTopic('retry', retryJob);
        setTimeout(() => {
          void this.kafkaProducer.publishJob(retryJob).catch((e) => {
            this.logger.error(`Retry publish failed: ${e}`);
          });
        }, 5000 * retryJob.attempt);
      } else {
        setTimeout(() => {
          this.localQueue.enqueue(retryJob);
        }, 3000 * retryJob.attempt);
      }
      return;
    }

    await this.store.savePointJob({
      id: pointJobId,
      sessionId: job.sessionId,
      pointId: job.pointId,
      pointTitle: job.pointTitle,
      govText: job.govText,
      status: 'failed',
      attempt: job.attempt,
      maxAttempts: job.maxAttempts,
      errorMessage: message,
      startedAt: createdAt,
      completedAt: new Date().toISOString(),
      createdAt,
      updatedAt: new Date().toISOString(),
    });

    if (this.kafkaConfig.isEnabled()) {
      await this.kafkaProducer.publishToTopic('dlq', { ...job, errorMessage: message });
    }

    await this.store.updateSessionCounts(job.sessionId);
    this.logger.error(`Dual verify failed ${job.sessionId}:${job.pointId}: ${message}`);
  }

  private isTransientError(message: string): boolean {
    const m = message.toLowerCase();
    return (
      m.includes('timeout') ||
      m.includes('429') ||
      m.includes('503') ||
      m.includes('502') ||
      m.includes('quota') ||
      m.includes('rate limit') ||
      m.includes('econnreset') ||
      m.includes('fetch failed') ||
      m.includes('network') ||
      m.includes('socket')
    );
  }
}
