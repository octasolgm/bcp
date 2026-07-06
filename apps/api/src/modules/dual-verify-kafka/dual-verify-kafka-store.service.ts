import { Injectable, Logger, OnModuleInit, BadRequestException } from '@nestjs/common';
import { SupabaseService } from '../../common/supabase/supabase.service';
import { DualVerifyKafkaFileStore } from './dual-verify-kafka-file-store';
import type {
  DualVerifyPointJobRecord,
  DualVerifySessionRecord,
  DualVerifySessionStatus,
  DualVerifyJobStatus,
} from './dual-verify-kafka.types';

export type DualVerifyPersistenceStatus = {
  dualVerifyTablesReady: boolean;
  complianceSessionsTableReady: boolean;
  fileFallbackReady: boolean;
  fileDataDir: string;
  mode: 'supabase' | 'file' | 'memory';
  hint?: string;
};

/** Session + point job persistence — Supabase → disk fallback → memory */
@Injectable()
export class DualVerifyKafkaStoreService implements OnModuleInit {
  private readonly logger = new Logger(DualVerifyKafkaStoreService.name);
  private readonly sessions = new Map<string, DualVerifySessionRecord>();
  private readonly points = new Map<string, DualVerifyPointJobRecord>();
  private readonly internalPdfBuffers = new Map<string, Buffer>();
  private readonly fileStore = new DualVerifyKafkaFileStore();
  private supabaseReady: boolean | null = null;
  private complianceTableReady: boolean | null = null;

  constructor(private readonly supabase: SupabaseService) {}

  async onModuleInit(): Promise<void> {
    for (const bundle of this.fileStore.hydrateAll()) {
      this.sessions.set(bundle.session.id, bundle.session);
      for (const point of bundle.points) {
        this.points.set(point.id, point);
      }
    }
    const count = this.sessions.size;
    if (count > 0) {
      this.logger.log(
        `Restored ${count} dual verify session(s) from disk (${this.fileStore.getDataDir()})`,
      );
    }
  }

  async isSupabaseReady(): Promise<boolean> {
    if (this.supabaseReady !== null) return this.supabaseReady;
    try {
      const { error } = await this.supabase
        .getAdminClient()
        .from('dual_verify_sessions')
        .select('id')
        .limit(1);
      this.supabaseReady = !error;
      if (error) {
        this.logger.warn(
          `dual_verify_sessions not in Supabase (${error.message}). Using disk fallback: ${this.fileStore.getDataDir()}`,
        );
      }
    } catch {
      this.supabaseReady = false;
    }
    return this.supabaseReady;
  }

  async isComplianceSessionsTableReady(): Promise<boolean> {
    if (this.complianceTableReady !== null) return this.complianceTableReady;
    try {
      const { error } = await this.supabase
        .getAdminClient()
        .from('landing_ai_compliance_sessions')
        .select('id')
        .limit(1);
      this.complianceTableReady = !error;
    } catch {
      this.complianceTableReady = false;
    }
    return this.complianceTableReady;
  }

  async getPersistenceStatus(): Promise<DualVerifyPersistenceStatus> {
    const dualVerifyTablesReady = await this.isSupabaseReady();
    const complianceSessionsTableReady =
      await this.isComplianceSessionsTableReady();
    const fileFallbackReady = this.fileStore.isEnabled();
    const fileDataDir = this.fileStore.getDataDir();

    let mode: DualVerifyPersistenceStatus['mode'] = 'memory';
    if (dualVerifyTablesReady) mode = 'supabase';
    else if (fileFallbackReady) mode = 'file';

    const hints: string[] = [];
    if (!dualVerifyTablesReady) {
      hints.push(
        'Apply docs/supabase/migrations/003_dual_verify_kafka.sql in Supabase SQL editor.',
      );
    }
    if (!complianceSessionsTableReady) {
      hints.push(
        'Apply docs/supabase/migrations/002_compliance_sessions.sql for cross-device reload.',
      );
    }
    if (mode === 'file') {
      hints.push(`Results saved to disk: ${fileDataDir} (survives API restart).`);
    }
    if (mode === 'memory') {
      hints.push('CRITICAL: No persistence — do not run paid jobs until migrations or disk path is fixed.');
    }

    return {
      dualVerifyTablesReady,
      complianceSessionsTableReady,
      fileFallbackReady,
      fileDataDir,
      mode,
      hint: hints.join(' '),
    };
  }

  /** Refuse paid runs when nothing durable is available */
  async assertPersistenceReady(): Promise<void> {
    const status = await this.getPersistenceStatus();
    if (status.mode === 'memory') {
      throw new BadRequestException(
        'Dual verify persistence unavailable. Apply Supabase migrations (see docs/supabase/APPLY_MIGRATIONS_MANUAL.md) or set DUAL_VERIFY_DATA_DIR.',
      );
    }
  }

  setInternalPdfBuffer(sessionId: string, buffer: Buffer): void {
    this.internalPdfBuffers.set(sessionId, buffer);
  }

  getInternalPdfBuffer(sessionId: string): Buffer | undefined {
    return this.internalPdfBuffers.get(sessionId);
  }

  private async persistToDisk(sessionId: string): Promise<void> {
    const session = this.sessions.get(sessionId);
    if (!session) return;
    const sessionPoints = [...this.points.values()].filter(
      (p) => p.sessionId === sessionId,
    );
    this.fileStore.saveSessionBundle(session, sessionPoints);
  }

  async saveSession(session: DualVerifySessionRecord): Promise<void> {
    this.sessions.set(session.id, session);

    if (await this.isSupabaseReady()) {
      await this.supabase.getAdminClient().from('dual_verify_sessions').upsert({
        id: session.id,
        status: session.status,
        granularity: session.granularity,
        gov_doc_id: session.govDocId,
        internal_doc_id: session.internalDocId,
        gov_file_hash: session.govFileHash,
        internal_file_hash: session.internalFileHash,
        gov_file_name: session.govFileName,
        internal_file_name: session.internalFileName,
        total_points: session.totalPoints,
        completed_points: session.completedPoints,
        failed_points: session.failedPoints,
        running_points: session.runningPoints,
        phase2_model: session.phase2Model,
        pipeline: session.pipeline,
        compliance_session_key: session.complianceSessionKey ?? null,
        summary_json: { transport: session.transport },
        completed_at: session.completedAt ?? null,
        updated_at: session.updatedAt,
      });
    }

    await this.persistToDisk(session.id);
  }

  async listRecentSessions(limit = 30): Promise<DualVerifySessionRecord[]> {
    const merged = new Map<string, DualVerifySessionRecord>();

    for (const s of this.sessions.values()) merged.set(s.id, s);
    for (const s of this.fileStore.listSessions(limit)) {
      if (!merged.has(s.id)) merged.set(s.id, s);
    }

    if (await this.isSupabaseReady()) {
      const { data } = await this.supabase
        .getAdminClient()
        .from('dual_verify_sessions')
        .select('*')
        .order('updated_at', { ascending: false })
        .limit(limit);
      for (const row of data ?? []) {
        const session = this.rowToSession(row as Record<string, unknown>);
        if (!merged.has(session.id)) merged.set(session.id, session);
      }
    }

    return [...merged.values()]
      .sort((a, b) => b.updatedAt.localeCompare(a.updatedAt))
      .slice(0, limit);
  }

  async getSession(sessionId: string): Promise<DualVerifySessionRecord | null> {
    const mem = this.sessions.get(sessionId);
    if (mem) return mem;

    const fromFile = this.fileStore.readBundle(sessionId);
    if (fromFile) {
      this.sessions.set(fromFile.session.id, fromFile.session);
      for (const p of fromFile.points) this.points.set(p.id, p);
      return fromFile.session;
    }

    if (!(await this.isSupabaseReady())) return null;

    const { data } = await this.supabase
      .getAdminClient()
      .from('dual_verify_sessions')
      .select('*')
      .eq('id', sessionId)
      .maybeSingle();

    if (!data) return null;

    const session = this.rowToSession(data as Record<string, unknown>);
    this.sessions.set(session.id, session);
    return session;
  }

  private rowToSession(row: Record<string, unknown>): DualVerifySessionRecord {
    return {
      id: String(row.id),
      status: row.status as DualVerifySessionStatus,
      granularity: row.granularity as DualVerifySessionRecord['granularity'],
      govDocId: String(row.gov_doc_id),
      internalDocId: String(row.internal_doc_id),
      govFileHash: String(row.gov_file_hash),
      internalFileHash: String(row.internal_file_hash),
      govFileName: String(row.gov_file_name ?? ''),
      internalFileName: String(row.internal_file_name ?? ''),
      totalPoints: Number(row.total_points ?? 0),
      completedPoints: Number(row.completed_points ?? 0),
      failedPoints: Number(row.failed_points ?? 0),
      runningPoints: Number(row.running_points ?? 0),
      queuedPoints: 0,
      phase2Model: String(row.phase2_model ?? ''),
      pipeline: 'kafka-dual-verify',
      complianceSessionKey: row.compliance_session_key
        ? String(row.compliance_session_key)
        : undefined,
      transport:
        (row.summary_json as { transport?: string })?.transport === 'kafka'
          ? 'kafka'
          : 'local',
      createdAt: String(row.created_at),
      updatedAt: String(row.updated_at),
      completedAt: row.completed_at ? String(row.completed_at) : null,
    };
  }

  async savePointJob(point: DualVerifyPointJobRecord): Promise<void> {
    this.points.set(point.id, point);

    if (await this.isSupabaseReady()) {
      await this.supabase.getAdminClient().from('dual_verify_point_jobs').upsert({
        id: point.id,
        session_id: point.sessionId,
        point_id: point.pointId,
        point_title: point.pointTitle ?? null,
        gov_text: point.govText,
        status: point.status,
        attempt: point.attempt,
        max_attempts: point.maxAttempts,
        landing_message: point.landingMessage ?? null,
        llm_message: point.llmMessage ?? null,
        agreement_json: point.agreementJson ?? null,
        error_message: point.errorMessage ?? null,
        started_at: point.startedAt ?? null,
        completed_at: point.completedAt ?? null,
        updated_at: point.updatedAt,
      });
    }

    await this.persistToDisk(point.sessionId);
  }

  async getPointJobs(sessionId: string): Promise<DualVerifyPointJobRecord[]> {
    const fromMem = [...this.points.values()].filter(
      (p) => p.sessionId === sessionId,
    );
    if (fromMem.length > 0) {
      return fromMem.sort((a, b) => a.pointId.localeCompare(b.pointId));
    }

    const fromFile = this.fileStore.readBundle(sessionId);
    if (fromFile?.points.length) {
      for (const p of fromFile.points) this.points.set(p.id, p);
      return fromFile.points;
    }

    if (!(await this.isSupabaseReady())) return [];

    const { data } = await this.supabase
      .getAdminClient()
      .from('dual_verify_point_jobs')
      .select('*')
      .eq('session_id', sessionId)
      .order('point_id');

    return (data ?? []).map((row) => this.rowToPoint(row as Record<string, unknown>));
  }

  async getPointJob(
    sessionId: string,
    pointId: string,
  ): Promise<DualVerifyPointJobRecord | null> {
    const found = [...this.points.values()].find(
      (p) => p.sessionId === sessionId && p.pointId === pointId,
    );
    if (found) return found;

    const fromFile = this.fileStore.readBundle(sessionId);
    const filePoint = fromFile?.points.find((p) => p.pointId === pointId);
    if (filePoint) {
      this.points.set(filePoint.id, filePoint);
      return filePoint;
    }

    if (!(await this.isSupabaseReady())) return null;

    const { data } = await this.supabase
      .getAdminClient()
      .from('dual_verify_point_jobs')
      .select('*')
      .eq('session_id', sessionId)
      .eq('point_id', pointId)
      .maybeSingle();

    return data ? this.rowToPoint(data as Record<string, unknown>) : null;
  }

  async updateSessionCounts(sessionId: string): Promise<DualVerifySessionRecord | null> {
    const session = await this.getSession(sessionId);
    if (!session) return null;

    const points = await this.getPointJobs(sessionId);
    const completedPoints = points.filter((p) => p.status === 'completed').length;
    const failedPoints = points.filter((p) => p.status === 'failed').length;
    const runningPoints = points.filter((p) => p.status === 'running').length;
    const queuedPoints = points.filter((p) => p.status === 'queued').length;

    let status: DualVerifySessionStatus = session.status;
    if (completedPoints + failedPoints >= session.totalPoints && session.totalPoints > 0) {
      status = failedPoints > 0 && completedPoints === 0 ? 'failed' : 'completed';
    } else if (runningPoints > 0 || completedPoints > 0 || failedPoints > 0) {
      status = 'processing';
    }

    const updated: DualVerifySessionRecord = {
      ...session,
      status,
      completedPoints,
      failedPoints,
      runningPoints,
      queuedPoints,
      updatedAt: new Date().toISOString(),
      completedAt:
        status === 'completed' || status === 'failed'
          ? new Date().toISOString()
          : session.completedAt,
    };

    await this.saveSession(updated);
    return updated;
  }

  private rowToPoint(row: Record<string, unknown>): DualVerifyPointJobRecord {
    const point: DualVerifyPointJobRecord = {
      id: String(row.id),
      sessionId: String(row.session_id),
      pointId: String(row.point_id),
      pointTitle: row.point_title ? String(row.point_title) : undefined,
      govText: String(row.gov_text),
      status: row.status as DualVerifyJobStatus,
      attempt: Number(row.attempt ?? 1),
      maxAttempts: Number(row.max_attempts ?? 3),
      landingMessage: row.landing_message ? String(row.landing_message) : undefined,
      llmMessage: row.llm_message ? String(row.llm_message) : undefined,
      agreementJson: row.agreement_json as DualVerifyPointJobRecord['agreementJson'],
      errorMessage: row.error_message ? String(row.error_message) : undefined,
      startedAt: row.started_at ? String(row.started_at) : null,
      completedAt: row.completed_at ? String(row.completed_at) : null,
      createdAt: String(row.created_at ?? new Date().toISOString()),
      updatedAt: String(row.updated_at ?? new Date().toISOString()),
    };
    this.points.set(point.id, point);
    return point;
  }
}
