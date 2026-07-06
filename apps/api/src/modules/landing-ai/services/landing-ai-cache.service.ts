import { createHash, randomUUID } from 'crypto';
import { Injectable, Logger } from '@nestjs/common';
import { SupabaseService } from '../../../common/supabase/supabase.service';
import type {
  ExtractSchemaKey,
  LandingAiJobRecord,
} from '../types/landing-ai.types';

/** Stored in landing_ai_extract_cache when compliance_sessions table is missing. */
export const COMPLIANCE_SESSION_CACHE_PREFIX = 'bcp-session:';

export type ComplianceSessionCachePayload = {
  kind: 'compliance_session';
  id: string;
  session_key: string;
  gov_file_hash: string;
  internal_file_hash: string;
  gov_file_name: string;
  internal_file_name: string;
  total_gov_points: number;
  compared_points: number;
  skipped_points: number;
  skipped_json: unknown;
  results_json: unknown;
  summary_json: unknown;
  created_at: string;
  updated_at: string;
};

@Injectable()
export class LandingAiCacheService {
  private readonly logger = new Logger(LandingAiCacheService.name);
  private cacheAvailable: boolean | null = null;

  constructor(private readonly supabaseService: SupabaseService) {}

  static hashBuffer(buffer: Buffer): string {
    return createHash('sha256').update(buffer).digest('hex');
  }

  async isCacheEnabled(): Promise<boolean> {
    if (this.cacheAvailable === true) return true;
    try {
      const client = this.supabaseService.getAdminClient();
      const { error } = await client
        .from('landing_ai_parse_cache')
        .select('id')
        .limit(1);
      if (!error) {
        this.cacheAvailable = true;
        return true;
      }
      this.logger.warn(
        `Supabase Landing AI cache tables not ready: ${error.message}. Run npm run db:migrate`,
      );
    } catch (e) {
      this.logger.warn('Supabase cache check failed', e);
    }
    this.cacheAvailable = false;
    return false;
  }

  async getParseCache(fileHash: string): Promise<{
    markdown: string;
    file_name: string;
  } | null> {
    if (!(await this.isCacheEnabled())) return null;
    const { data, error } = await this.supabaseService
      .getAdminClient()
      .from('landing_ai_parse_cache')
      .select('markdown, file_name')
      .eq('file_hash', fileHash)
      .maybeSingle();
    if (error || !data) return null;
    return data as { markdown: string; file_name: string };
  }

  async saveParseCache(params: {
    fileHash: string;
    fileName: string;
    markdown: string;
    parseModel: string;
    creditUsage?: number;
    chunksJson?: unknown;
  }): Promise<void> {
    if (!(await this.isCacheEnabled())) return;
    await this.supabaseService.getAdminClient().from('landing_ai_parse_cache').upsert(
      {
        file_hash: params.fileHash,
        file_name: params.fileName,
        markdown: params.markdown,
        parse_model: params.parseModel,
        credit_usage: params.creditUsage ?? null,
        chunks_json: params.chunksJson ?? null,
        updated_at: new Date().toISOString(),
      },
      { onConflict: 'file_hash' },
    );
  }

  async getExtractCache(
    fileHash: string,
    schemaKey: ExtractSchemaKey,
  ): Promise<{ points_json: unknown } | null> {
    if (!(await this.isCacheEnabled())) return null;
    const { data, error } = await this.supabaseService
      .getAdminClient()
      .from('landing_ai_extract_cache')
      .select('points_json')
      .eq('file_hash', fileHash)
      .eq('schema_key', schemaKey)
      .maybeSingle();
    if (error || !data) return null;
    return data as { points_json: unknown };
  }

  async saveExtractCache(params: {
    fileHash: string;
    schemaKey: ExtractSchemaKey;
    pointsJson: unknown;
    extractModel: string;
    creditUsage?: number;
  }): Promise<void> {
    if (!(await this.isCacheEnabled())) return;
    await this.supabaseService
      .getAdminClient()
      .from('landing_ai_extract_cache')
      .upsert(
        {
          file_hash: params.fileHash,
          schema_key: params.schemaKey,
          points_json: params.pointsJson,
          extract_model: params.extractModel,
          credit_usage: params.creditUsage ?? null,
        },
        { onConflict: 'file_hash,schema_key' },
      );
  }

  async getCompareCache(
    compareKey: string,
  ): Promise<{ points_json: unknown } | null> {
    return this.getExtractCache(compareKey, 'compliance_comparison');
  }

  async saveCompareCache(params: {
    compareKey: string;
    comparisonJson: unknown;
    extractModel: string;
    creditUsage?: number;
  }): Promise<void> {
    return this.saveExtractCache({
      fileHash: params.compareKey,
      schemaKey: 'compliance_comparison',
      pointsJson: params.comparisonJson,
      extractModel: params.extractModel,
      creditUsage: params.creditUsage,
    });
  }

  static compareCacheKey(
    internalFileHash: string,
    pointId: string,
    promptVersion = 'v2',
  ): string {
    // Bump revision when v2 prompt logic changes (e.g. semantic-intent compare).
    const revision = promptVersion === 'v1' ? 'v1' : 'v2-semantic';
    return createHash('sha256')
      .update(`${internalFileHash}:${pointId}:${revision}`)
      .digest('hex');
  }

  static sessionKey(
    govFileHash: string,
    internalFileHash: string,
    compareGranularity = 'section',
  ): string {
    return createHash('sha256')
      .update(`${govFileHash}:${internalFileHash}:${compareGranularity}`)
      .digest('hex');
  }

  async getCacheStatus(builtinHashes: string[]): Promise<{
    parse: Record<string, boolean>;
    extract: Record<string, boolean>;
    jobsCount: number;
    sessionsCount: number;
  }> {
    const empty = {
      parse: {} as Record<string, boolean>,
      extract: {} as Record<string, boolean>,
      jobsCount: 0,
      sessionsCount: 0,
    };
    if (!(await this.isCacheEnabled())) return empty;

    const client = this.supabaseService.getAdminClient();

    for (const hash of builtinHashes) {
      const { data: parseRow } = await client
        .from('landing_ai_parse_cache')
        .select('file_hash')
        .eq('file_hash', hash)
        .maybeSingle();
      empty.parse[hash] = Boolean(parseRow);

      const { data: extractRows } = await client
        .from('landing_ai_extract_cache')
        .select('schema_key')
        .eq('file_hash', hash);
      empty.extract[hash] = (extractRows?.length ?? 0) > 0;
    }

    const { count: jobsCount } = await client
      .from('landing_ai_jobs')
      .select('*', { count: 'exact', head: true });

    let sessionsCount = 0;
    try {
      const { count } = await client
        .from('landing_ai_compliance_sessions')
        .select('*', { count: 'exact', head: true });
      sessionsCount = count ?? 0;
    } catch {
      sessionsCount = 0;
    }

    empty.jobsCount = jobsCount ?? 0;
    empty.sessionsCount = sessionsCount;
    return empty;
  }

  async saveComplianceSession(params: {
    sessionKey: string;
    govFileHash: string;
    internalFileHash: string;
    govFileName: string;
    internalFileName: string;
    totalGovPoints: number;
    comparedPoints: number;
    skippedPoints: number;
    skippedJson: unknown;
    resultsJson: unknown;
    summaryJson?: unknown;
  }): Promise<{ saved: boolean; comparedPoints: number }> {
    if (!(await this.isCacheEnabled())) {
      throw new Error('Supabase cache is not configured');
    }
    const { error } = await this.supabaseService
      .getAdminClient()
      .from('landing_ai_compliance_sessions')
      .upsert(
        {
          session_key: params.sessionKey,
          gov_file_hash: params.govFileHash,
          internal_file_hash: params.internalFileHash,
          gov_file_name: params.govFileName,
          internal_file_name: params.internalFileName,
          total_gov_points: params.totalGovPoints,
          compared_points: params.comparedPoints,
          skipped_points: params.skippedPoints,
          skipped_json: params.skippedJson,
          results_json: params.resultsJson,
          summary_json: params.summaryJson ?? null,
          updated_at: new Date().toISOString(),
        },
        { onConflict: 'session_key' },
      );
    if (error) {
      throw new Error(error.message);
    }
    return { saved: true, comparedPoints: params.comparedPoints };
  }

  async isComplianceSessionsTableReady(): Promise<boolean> {
    if (!(await this.isCacheEnabled())) return false;
    const { error } = await this.supabaseService
      .getAdminClient()
      .from('landing_ai_compliance_sessions')
      .select('id')
      .limit(1);
    return !error;
  }

  async countCompareCacheRows(): Promise<number> {
    if (!(await this.isCacheEnabled())) return 0;
    const { count, error } = await this.supabaseService
      .getAdminClient()
      .from('landing_ai_extract_cache')
      .select('*', { count: 'exact', head: true })
      .eq('schema_key', 'compliance_comparison');
    if (error) {
      this.logger.warn(`countCompareCacheRows failed: ${error.message}`);
      return 0;
    }
    return count ?? 0;
  }

  async listComplianceSessions(limit = 30): Promise<
    Array<{
      id: string;
      session_key: string;
      gov_file_name: string;
      internal_file_name: string;
      total_gov_points: number;
      compared_points: number;
      skipped_points: number;
      summary_json: unknown;
      created_at: string;
      updated_at: string;
    }>
  > {
    if (!(await this.isCacheEnabled())) return [];
    const { data, error } = await this.supabaseService
      .getAdminClient()
      .from('landing_ai_compliance_sessions')
      .select(
        'id, session_key, gov_file_name, internal_file_name, total_gov_points, compared_points, skipped_points, summary_json, created_at, updated_at',
      )
      .order('created_at', { ascending: false })
      .limit(limit);
    if (error) {
      this.logger.warn(`listComplianceSessions failed: ${error.message}`);
      return [];
    }
    return data ?? [];
  }

  async getComplianceSessionByKey(sessionKey: string): Promise<{
    id: string;
    session_key: string;
    results_json: unknown;
    compared_points: number;
  } | null> {
    if (!(await this.isCacheEnabled())) return null;
    const { data, error } = await this.supabaseService
      .getAdminClient()
      .from('landing_ai_compliance_sessions')
      .select('id, session_key, results_json, compared_points')
      .eq('session_key', sessionKey)
      .maybeSingle();
    if (error || !data) return null;
    return data;
  }

  async getComplianceSessionById(id: string): Promise<{
    id: string;
    session_key: string;
    gov_file_name: string;
    internal_file_name: string;
    total_gov_points: number;
    compared_points: number;
    skipped_points: number;
    skipped_json: unknown;
    results_json: unknown;
    summary_json: unknown;
    created_at: string;
  } | null> {
    if (!(await this.isCacheEnabled())) return null;
    const { data, error } = await this.supabaseService
      .getAdminClient()
      .from('landing_ai_compliance_sessions')
      .select(
        'id, session_key, gov_file_name, internal_file_name, total_gov_points, compared_points, skipped_points, skipped_json, results_json, summary_json, created_at',
      )
      .eq('id', id)
      .maybeSingle();
    if (error || !data) return null;
    return data;
  }

  static complianceSessionCacheFileHash(sessionKey: string): string {
    return `${COMPLIANCE_SESSION_CACHE_PREFIX}${sessionKey}`;
  }

  private isComplianceSessionCacheRow(
    fileHash: string,
    pointsJson: unknown,
  ): pointsJson is ComplianceSessionCachePayload {
    return (
      fileHash.startsWith(COMPLIANCE_SESSION_CACHE_PREFIX) &&
      typeof pointsJson === 'object' &&
      pointsJson !== null &&
      (pointsJson as ComplianceSessionCachePayload).kind === 'compliance_session'
    );
  }

  async saveComplianceSessionToExtractCache(params: {
    sessionKey: string;
    govFileHash: string;
    internalFileHash: string;
    govFileName: string;
    internalFileName: string;
    totalGovPoints: number;
    comparedPoints: number;
    skippedPoints: number;
    skippedJson: unknown;
    resultsJson: unknown;
    summaryJson?: unknown;
    existingId?: string;
  }): Promise<{ saved: boolean; comparedPoints: number; id: string }> {
    if (!(await this.isCacheEnabled())) {
      throw new Error('Supabase cache is not configured');
    }

    const existing = await this.getComplianceSessionFromExtractCacheByKey(
      params.sessionKey,
    );
    const now = new Date().toISOString();
    const payload: ComplianceSessionCachePayload = {
      kind: 'compliance_session',
      id: params.existingId ?? existing?.id ?? randomUUID(),
      session_key: params.sessionKey,
      gov_file_hash: params.govFileHash,
      internal_file_hash: params.internalFileHash,
      gov_file_name: params.govFileName,
      internal_file_name: params.internalFileName,
      total_gov_points: params.totalGovPoints,
      compared_points: params.comparedPoints,
      skipped_points: params.skippedPoints,
      skipped_json: params.skippedJson,
      results_json: params.resultsJson,
      summary_json: params.summaryJson ?? null,
      created_at: existing?.created_at ?? now,
      updated_at: now,
    };

    const { error } = await this.supabaseService
      .getAdminClient()
      .from('landing_ai_extract_cache')
      .upsert(
        {
          file_hash: LandingAiCacheService.complianceSessionCacheFileHash(
            params.sessionKey,
          ),
          schema_key: 'compliance_comparison',
          points_json: payload,
          extract_model: 'compliance_session_v1',
        },
        { onConflict: 'file_hash,schema_key' },
      );
    if (error) {
      throw new Error(error.message);
    }
    return {
      saved: true,
      comparedPoints: params.comparedPoints,
      id: payload.id,
    };
  }

  async listComplianceSessionsFromExtractCache(limit = 30): Promise<
    Array<{
      id: string;
      session_key: string;
      gov_file_name: string;
      internal_file_name: string;
      total_gov_points: number;
      compared_points: number;
      skipped_points: number;
      summary_json: unknown;
      created_at: string;
      updated_at: string;
    }>
  > {
    if (!(await this.isCacheEnabled())) return [];
    const { data, error } = await this.supabaseService
      .getAdminClient()
      .from('landing_ai_extract_cache')
      .select('file_hash, points_json, created_at')
      .eq('schema_key', 'compliance_comparison')
      .like('file_hash', `${COMPLIANCE_SESSION_CACHE_PREFIX}%`)
      .order('created_at', { ascending: false })
      .limit(Math.max(limit, 50));

    if (error) {
      this.logger.warn(
        `listComplianceSessionsFromExtractCache failed: ${error.message}`,
      );
      return [];
    }

    return (data ?? [])
      .map((row) => {
        if (
          !this.isComplianceSessionCacheRow(row.file_hash, row.points_json)
        ) {
          return null;
        }
        const p = row.points_json;
        return {
          id: p.id,
          session_key: p.session_key,
          gov_file_name: p.gov_file_name,
          internal_file_name: p.internal_file_name,
          total_gov_points: p.total_gov_points,
          compared_points: p.compared_points,
          skipped_points: p.skipped_points,
          summary_json: p.summary_json,
          created_at: p.created_at,
          updated_at: p.updated_at,
        };
      })
      .filter((row): row is NonNullable<typeof row> => row !== null)
      .slice(0, limit);
  }

  async getComplianceSessionFromExtractCacheByKey(
    sessionKey: string,
  ): Promise<ComplianceSessionCachePayload | null> {
    if (!(await this.isCacheEnabled())) return null;
    const row = await this.getExtractCache(
      LandingAiCacheService.complianceSessionCacheFileHash(sessionKey),
      'compliance_comparison',
    );
    if (!row?.points_json) return null;
    const fileHash = LandingAiCacheService.complianceSessionCacheFileHash(
      sessionKey,
    );
    if (
      !this.isComplianceSessionCacheRow(fileHash, row.points_json)
    ) {
      return null;
    }
    return row.points_json;
  }

  async getComplianceSessionFromExtractCacheById(
    id: string,
  ): Promise<ComplianceSessionCachePayload | null> {
    if (!(await this.isCacheEnabled())) return null;
    const { data, error } = await this.supabaseService
      .getAdminClient()
      .from('landing_ai_extract_cache')
      .select('file_hash, points_json')
      .eq('schema_key', 'compliance_comparison')
      .like('file_hash', `${COMPLIANCE_SESSION_CACHE_PREFIX}%`);

    if (error || !data?.length) return null;
    for (const row of data) {
      if (
        this.isComplianceSessionCacheRow(row.file_hash, row.points_json) &&
        row.points_json.id === id
      ) {
        return row.points_json;
      }
    }
    return null;
  }

  async logJob(params: {
    operation: 'parse' | 'extract_gov' | 'extract_internal' | 'compare';
    fileName: string;
    fileHash: string;
    fileSizeBytes: number;
    status: 'success' | 'error' | 'pending';
    creditUsage?: number;
    durationMs?: number;
    landingJobId?: string;
    model?: string;
    errorMessage?: string;
    responseJson?: unknown;
  }): Promise<void> {
    if (!(await this.isCacheEnabled())) return;
    await this.supabaseService.getAdminClient().from('landing_ai_jobs').insert({
      operation: params.operation,
      file_name: params.fileName,
      file_hash: params.fileHash,
      file_size_bytes: params.fileSizeBytes,
      status: params.status,
      credit_usage: params.creditUsage ?? null,
      duration_ms: params.durationMs ?? null,
      landing_job_id: params.landingJobId ?? null,
      model: params.model ?? null,
      error_message: params.errorMessage ?? null,
      response_json: params.responseJson ?? null,
    });
  }

  async listJobs(limit = 20): Promise<LandingAiJobRecord[]> {
    if (!(await this.isCacheEnabled())) return [];
    const { data, error } = await this.supabaseService
      .getAdminClient()
      .from('landing_ai_jobs')
      .select(
        'id, operation, file_name, file_hash, status, credit_usage, duration_ms, landing_job_id, model, error_message, created_at',
      )
      .order('created_at', { ascending: false })
      .limit(limit);
    if (error) {
      this.logger.warn(`listJobs failed: ${error.message}`);
      return [];
    }
    return (data ?? []) as LandingAiJobRecord[];
  }
}
