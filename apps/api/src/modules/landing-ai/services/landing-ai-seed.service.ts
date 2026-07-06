import { Injectable, Logger } from '@nestjs/common';
import { readFileSync } from 'fs';
import { join } from 'path';
import { BUILTIN_EXTRACT_DOCS, type BuiltinExtractDoc } from '../builtin-docs';
import { LandingAiCacheService } from './landing-ai-cache.service';
import { filterComparableGovPoints } from '../utils/gov-point-filter';
import type {
  ExtractSchemaKey,
  GovRequirementPoint,
  LandingAiExtractResponse,
} from '../types/landing-ai.types';

type SeedExtractFile = {
  fileName: string;
  fileHash: string;
  schemaKey: ExtractSchemaKey;
  points: GovRequirementPoint[];
  creditUsage?: number;
  jobId?: string;
  durationMs?: number;
};

@Injectable()
export class LandingAiSeedService {
  private readonly logger = new Logger(LandingAiSeedService.name);

  constructor(private readonly cache: LandingAiCacheService) {}

  listBuiltinDocs() {
    return BUILTIN_EXTRACT_DOCS.map((d) => ({
      id: d.id,
      label: d.label,
      fileName: d.fileName,
      fileHash: d.fileHash,
      schemaKey: d.schemaKey,
      pointCount: d.pointCount,
    }));
  }

  loadSeedFile(seedFile: string): SeedExtractFile {
    const path = join(__dirname, '..', 'seed-data', seedFile);
    const raw = readFileSync(path, 'utf-8');
    const data = JSON.parse(raw) as SeedExtractFile;
    if (!data.fileHash || !data.schemaKey || !Array.isArray(data.points)) {
      throw new Error(`Invalid seed file: ${seedFile}`);
    }
    return data;
  }

  async seedBuiltinDoc(doc: BuiltinExtractDoc) {
    const data = this.loadSeedFile(doc.seedFile);
    const enabled = await this.cache.isCacheEnabled();
    if (!enabled) {
      throw new Error(
        'Supabase cache tables missing. Run npm run db:migrate',
      );
    }

    await this.cache.saveExtractCache({
      fileHash: data.fileHash,
      schemaKey: data.schemaKey,
      pointsJson: { points: data.points },
      extractModel: 'seed-builtin',
      creditUsage: data.creditUsage ?? 0,
    });

    await this.cache.logJob({
      operation:
        data.schemaKey === 'gov_requirement_points'
          ? 'extract_gov'
          : 'extract_internal',
      fileName: data.fileName,
      fileHash: data.fileHash,
      fileSizeBytes: 0,
      status: 'success',
      creditUsage: data.creditUsage ?? 0,
      durationMs: data.durationMs,
      landingJobId: data.jobId,
      model: 'seed-builtin',
      errorMessage: 'seeded_from_file',
      responseJson: { pointCount: data.points.length },
    });

    this.logger.log(
      `Seeded ${data.points.length} points for ${data.fileName} (${data.fileHash.slice(0, 8)}…)`,
    );

    return {
      success: true,
      id: doc.id,
      fileName: data.fileName,
      fileHash: data.fileHash,
      schemaKey: data.schemaKey,
      pointCount: data.points.length,
      creditUsage: data.creditUsage ?? 0,
    };
  }

  async seedAllBuiltin() {
    const results = [];
    for (const doc of BUILTIN_EXTRACT_DOCS) {
      results.push(await this.seedBuiltinDoc(doc));
    }
    return { success: true, seeded: results };
  }

  /** Load points from repo seed JSON when Supabase extract cache is empty (dev fallback). */
  private loadPointsFromSeedFile(
    fileHash: string,
    schemaKey: ExtractSchemaKey,
  ): GovRequirementPoint[] | null {
    const doc = BUILTIN_EXTRACT_DOCS.find(
      (d) => d.fileHash === fileHash && d.schemaKey === schemaKey,
    );
    if (!doc) return null;
    try {
      const data = this.loadSeedFile(doc.seedFile);
      return data.points?.length ? data.points : null;
    } catch (err) {
      this.logger.warn(
        `Seed file fallback failed for ${doc.id}: ${err instanceof Error ? err.message : err}`,
      );
      return null;
    }
  }

  private buildStoredPointsResponse(
    fileHash: string,
    schemaKey: ExtractSchemaKey,
    points: GovRequirementPoint[],
    cached: boolean,
  ): LandingAiExtractResponse {
    const doc = BUILTIN_EXTRACT_DOCS.find(
      (d) => d.fileHash === fileHash && d.schemaKey === schemaKey,
    );

    return {
      success: true,
      cached,
      fileName: doc?.fileName ?? 'document.pdf',
      fileHash,
      schemaKey,
      pointCount: points.length,
      points,
      creditUsage: 0,
      ...(schemaKey === 'gov_requirement_points'
        ? (() => {
            const { comparable, skipped } = filterComparableGovPoints(points);
            return {
              comparablePoints: comparable,
              comparableCount: comparable.length,
              skippedPoints: skipped.map((s) => ({
                point_id: s.point.point_id,
                title: s.point.title,
                reason: s.reason,
              })),
              skippedCount: skipped.length,
            };
          })()
        : {}),
    };
  }

  async getStoredPoints(
    fileHash: string,
    schemaKey: ExtractSchemaKey,
  ): Promise<LandingAiExtractResponse | null> {
    const cached = await this.cache.getExtractCache(fileHash, schemaKey);
    if (cached?.points_json) {
      const obj = cached.points_json as { points?: GovRequirementPoint[] };
      const points = Array.isArray(obj.points) ? obj.points : [];
      if (points.length) {
        return this.buildStoredPointsResponse(fileHash, schemaKey, points, true);
      }
    }

    const seedPoints = this.loadPointsFromSeedFile(fileHash, schemaKey);
    if (seedPoints?.length) {
      this.logger.log(
        `Using builtin seed file for ${schemaKey} (${seedPoints.length} points) — Supabase cache empty`,
      );
      return this.buildStoredPointsResponse(fileHash, schemaKey, seedPoints, false);
    }

    return null;
  }
}
