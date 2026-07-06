import type { DualVerifyAgreement } from './utils/dual-verify-agreement';

export const DUAL_VERIFY_JOB_SCHEMA_VERSION = '1.0' as const;

export type DualVerifyGranularity = 'section' | 'leaf';

export type DualVerifyJobStatus =
  | 'queued'
  | 'running'
  | 'completed'
  | 'failed'
  | 'cancelled';

export type DualVerifySessionStatus =
  | 'queued'
  | 'processing'
  | 'completed'
  | 'failed'
  | 'cancelled';

/** Kafka / local queue message — one gov point */
export interface DualVerifyJobMessage {
  schemaVersion: typeof DUAL_VERIFY_JOB_SCHEMA_VERSION;
  messageId: string;
  jobId: string;
  sessionId: string;
  pointId: string;
  pointTitle?: string;
  govText: string;
  granularity: DualVerifyGranularity;
  govDocId: string;
  internalDocId: string;
  govFileHash: string;
  internalFileHash: string;
  govFileName: string;
  internalFileName: string;
  phase2Model: string;
  attempt: number;
  maxAttempts: number;
  forceRefresh?: boolean;
  correlationId: string;
  createdAt: string;
}

export interface DualVerifySessionRecord {
  id: string;
  status: DualVerifySessionStatus;
  granularity: DualVerifyGranularity;
  govDocId: string;
  internalDocId: string;
  govFileHash: string;
  internalFileHash: string;
  govFileName: string;
  internalFileName: string;
  totalPoints: number;
  completedPoints: number;
  failedPoints: number;
  runningPoints: number;
  queuedPoints: number;
  phase2Model: string;
  pipeline: 'kafka-dual-verify';
  complianceSessionKey?: string;
  transport: 'kafka' | 'local';
  createdAt: string;
  updatedAt: string;
  completedAt?: string | null;
}

export interface DualVerifyPointJobRecord {
  id: string;
  sessionId: string;
  pointId: string;
  pointTitle?: string;
  govText: string;
  status: DualVerifyJobStatus;
  attempt: number;
  maxAttempts: number;
  landingMessage?: string;
  llmMessage?: string;
  agreementJson?: DualVerifyAgreement;
  errorMessage?: string;
  startedAt?: string | null;
  completedAt?: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface CreateDualVerifyJobDto {
  pointIds: string[];
  granularity?: DualVerifyGranularity;
  govDocId?: string;
  internalDocId?: string;
  phase2Model?: string;
  maxAttempts?: number;
  /** When true, re-run Landing AI even if Supabase compare cache exists (costs credits) */
  forceRefresh?: boolean;
}

export interface DualVerifySessionProgress {
  session: DualVerifySessionRecord;
  points: DualVerifyPointJobRecord[];
}

export interface DualVerifyHealthResponse {
  status: 'ok';
  transport: 'kafka' | 'local';
  kafkaConfigured: boolean;
  topics: {
    jobs: string;
    retry: string;
    dlq: string;
    results: string;
  };
  persistence: {
    dualVerifyTablesReady: boolean;
    complianceSessionsTableReady: boolean;
    fileFallbackReady: boolean;
    fileDataDir: string;
    mode: 'supabase' | 'file' | 'memory';
    hint?: string;
  };
}
