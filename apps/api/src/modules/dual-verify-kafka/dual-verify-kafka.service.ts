import {
  BadRequestException,
  Injectable,
  NotFoundException,
} from '@nestjs/common';
import { randomUUID } from 'crypto';
import { findBuiltinDoc } from '../landing-ai/builtin-docs';
import { LandingAiSeedService } from '../landing-ai/services/landing-ai-seed.service';
import {
  filterComparableGovLeafPoints,
  filterComparableGovPoints,
} from '../landing-ai/utils/gov-point-filter';
import type { GovRequirementPoint } from '../landing-ai/types/landing-ai.types';
import { KafkaConfigService } from '../kafka/kafka.config';
import { KafkaProducerService } from '../kafka/kafka-producer.service';
import { LocalJobQueueService } from '../kafka/local-job-queue.service';
import { DualVerifyKafkaStoreService } from './dual-verify-kafka-store.service';
import {
  DUAL_VERIFY_JOB_SCHEMA_VERSION,
  type CreateDualVerifyJobDto,
  type DualVerifyHealthResponse,
  type DualVerifyJobMessage,
  type DualVerifyPointJobRecord,
  type DualVerifySessionProgress,
  type DualVerifySessionRecord,
} from './dual-verify-kafka.types';

@Injectable()
export class DualVerifyKafkaService {
  constructor(
    private readonly seed: LandingAiSeedService,
    private readonly store: DualVerifyKafkaStoreService,
    private readonly kafkaConfig: KafkaConfigService,
    private readonly kafkaProducer: KafkaProducerService,
    private readonly localQueue: LocalJobQueueService,
  ) {}

  async getHealth(): Promise<DualVerifyHealthResponse> {
    const persistence = await this.store.getPersistenceStatus();

    return {
      status: 'ok',
      transport: this.kafkaConfig.getTransportMode(),
      kafkaConfigured: this.kafkaConfig.isKafkaConfigured(),
      topics: {
        jobs: this.kafkaConfig.getTopicJobs(),
        retry: this.kafkaConfig.getTopicRetry(),
        dlq: this.kafkaConfig.getTopicDlq(),
        results: this.kafkaConfig.getTopicResults(),
      },
      persistence,
    };
  }

  /** Create session + enqueue one message per gov point */
  async createJob(
    dto: CreateDualVerifyJobDto,
    internalPdfBuffer?: Buffer,
  ): Promise<DualVerifySessionRecord> {
    await this.store.assertPersistenceReady();

    if (!dto.pointIds?.length) {
      throw new BadRequestException('pointIds must contain at least one gov point');
    }

    const govDocId = dto.govDocId ?? 'gov-tfs-guidelines';
    const internalDocId = dto.internalDocId ?? 'internal-imptfs';
    const govDoc = findBuiltinDoc(govDocId);
    const internalDoc = findBuiltinDoc(internalDocId);

    if (!govDoc || !internalDoc) {
      throw new BadRequestException('Invalid govDocId or internalDocId');
    }

    let stored = await this.seed.getStoredPoints(
      govDoc.fileHash,
      govDoc.schemaKey,
    );
    if (!stored?.points?.length) {
      try {
        await this.seed.seedBuiltinDoc(govDoc);
        stored = await this.seed.getStoredPoints(
          govDoc.fileHash,
          govDoc.schemaKey,
        );
      } catch {
        /* seed may fail if Supabase down — getStoredPoints also reads local seed files */
      }
    }
    if (!stored?.points?.length) {
      throw new BadRequestException(
        'Gov points not available. Click “Seed builtin docs” in the UI or run POST /landing-ai/seed/builtin.',
      );
    }

    const granularity = dto.granularity ?? 'section';
    const filter =
      granularity === 'leaf'
        ? filterComparableGovLeafPoints
        : filterComparableGovPoints;

    const filtered = filter(stored.points as GovRequirementPoint[]);
    const selected = filtered.comparable.filter((p) =>
      dto.pointIds.includes(p.point_id),
    );

    if (!selected.length) {
      throw new BadRequestException(
        'No matching comparable gov points for selected pointIds',
      );
    }

    const sessionId = randomUUID();
    const correlationId = randomUUID();
    const now = new Date().toISOString();
    const phase2Model =
      dto.phase2Model ??
      process.env.GEMINI_DEFAULT_MODEL ??
      'gemini-2.5-flash-lite';
    const maxAttempts = dto.maxAttempts ?? this.kafkaConfig.getMaxAttempts();
    const transport = this.kafkaConfig.getTransportMode();

    if (internalPdfBuffer?.length) {
      this.store.setInternalPdfBuffer(sessionId, internalPdfBuffer);
    }

    const session: DualVerifySessionRecord = {
      id: sessionId,
      status: 'queued',
      granularity,
      govDocId,
      internalDocId,
      govFileHash: govDoc.fileHash,
      internalFileHash: internalDoc.fileHash,
      govFileName: govDoc.fileName,
      internalFileName: internalDoc.fileName,
      totalPoints: selected.length,
      completedPoints: 0,
      failedPoints: 0,
      runningPoints: 0,
      queuedPoints: selected.length,
      phase2Model,
      pipeline: 'kafka-dual-verify',
      transport,
      createdAt: now,
      updatedAt: now,
      completedAt: null,
    };

    await this.store.saveSession(session);

    const messages: DualVerifyJobMessage[] = selected.map((point) => ({
      schemaVersion: DUAL_VERIFY_JOB_SCHEMA_VERSION,
      messageId: randomUUID(),
      jobId: randomUUID(),
      sessionId,
      pointId: point.point_id,
      pointTitle: point.title,
      govText: point.text,
      granularity,
      govDocId,
      internalDocId,
      govFileHash: govDoc.fileHash,
      internalFileHash: internalDoc.fileHash,
      govFileName: govDoc.fileName,
      internalFileName: internalDoc.fileName,
      phase2Model,
      attempt: 1,
      maxAttempts,
      forceRefresh: dto.forceRefresh ?? false,
      correlationId,
      createdAt: now,
    }));

    for (const msg of messages) {
      const pointRecord: DualVerifyPointJobRecord = {
        id: msg.jobId,
        sessionId,
        pointId: msg.pointId,
        pointTitle: msg.pointTitle,
        govText: msg.govText,
        status: 'queued',
        attempt: 1,
        maxAttempts,
        createdAt: now,
        updatedAt: now,
      };
      await this.store.savePointJob(pointRecord);
    }

    if (transport === 'kafka') {
      for (const msg of messages) {
        await this.kafkaProducer.publishJob(msg);
      }
      // Dev reliability: also process via in-process queue (Kafka still receives messages for Azure monitoring)
      if (process.env.NODE_ENV === 'development') {
        this.localQueue.enqueueMany(messages);
      }
    } else {
      this.localQueue.enqueueMany(messages);
    }

    return { ...session, status: 'processing' };
  }

  async listSessions(limit = 30): Promise<DualVerifySessionRecord[]> {
    return this.store.listRecentSessions(limit);
  }

  async getProgress(sessionId: string): Promise<DualVerifySessionProgress> {
    const session = await this.store.updateSessionCounts(sessionId);
    if (!session) {
      throw new NotFoundException(`Dual verify session not found: ${sessionId}`);
    }
    const points = await this.store.getPointJobs(sessionId);
    return { session, points };
  }

  async getResults(sessionId: string): Promise<DualVerifyPointJobRecord[]> {
    const progress = await this.getProgress(sessionId);
    return progress.points.filter((p) => p.status === 'completed' || p.status === 'failed');
  }

  async retryFailed(sessionId: string): Promise<{ requeued: number }> {
    const session = await this.store.getSession(sessionId);
    if (!session) {
      throw new NotFoundException(`Session not found: ${sessionId}`);
    }

    const points = await this.store.getPointJobs(sessionId);
    const failed = points.filter((p) => p.status === 'failed');
    let requeued = 0;

    for (const point of failed) {
      const msg: DualVerifyJobMessage = {
        schemaVersion: DUAL_VERIFY_JOB_SCHEMA_VERSION,
        messageId: randomUUID(),
        jobId: point.id,
        sessionId,
        pointId: point.pointId,
        pointTitle: point.pointTitle,
        govText: point.govText,
        granularity: session.granularity,
        govDocId: session.govDocId,
        internalDocId: session.internalDocId,
        govFileHash: session.govFileHash,
        internalFileHash: session.internalFileHash,
        govFileName: session.govFileName,
        internalFileName: session.internalFileName,
        phase2Model: session.phase2Model,
        attempt: 1,
        maxAttempts: point.maxAttempts,
        correlationId: randomUUID(),
        createdAt: new Date().toISOString(),
      };

      await this.store.savePointJob({
        ...point,
        status: 'queued',
        attempt: 1,
        errorMessage: undefined,
        updatedAt: new Date().toISOString(),
      });

      if (session.transport === 'kafka') {
        await this.kafkaProducer.publishJob(msg);
      } else {
        this.localQueue.enqueue(msg);
      }
      requeued += 1;
    }

    await this.store.updateSessionCounts(sessionId);
    return { requeued };
  }
}
