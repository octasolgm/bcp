import { existsSync, mkdirSync, readdirSync, readFileSync, writeFileSync } from 'fs';
import { dirname, join, resolve } from 'path';
import type {
  DualVerifyPointJobRecord,
  DualVerifySessionRecord,
} from './dual-verify-kafka.types';

type SessionFile = {
  session: DualVerifySessionRecord;
  points: DualVerifyPointJobRecord[];
};

function resolveDataDir(): string {
  const envDir = process.env.DUAL_VERIFY_DATA_DIR?.trim();
  if (envDir) return resolve(envDir);
  const candidates = [
    resolve(process.cwd(), 'data', 'dual-verify-kafka'),
    resolve(process.cwd(), 'apps', 'api', 'data', 'dual-verify-kafka'),
  ];
  for (const dir of candidates) {
    const parent = dirname(dir);
    if (existsSync(parent) || dir.includes('dual-verify-kafka')) {
      return dir;
    }
  }
  return candidates[0];
}

/** Disk fallback when Supabase dual_verify tables are missing — survives API restarts */
export class DualVerifyKafkaFileStore {
  private readonly dir: string;

  constructor() {
    this.dir = resolveDataDir();
    if (!existsSync(this.dir)) {
      mkdirSync(this.dir, { recursive: true });
    }
  }

  getDataDir(): string {
    return this.dir;
  }

  isEnabled(): boolean {
    return true;
  }

  saveSessionBundle(session: DualVerifySessionRecord, points: DualVerifyPointJobRecord[]): void {
    const filePath = join(this.dir, `${session.id}.json`);
    const existing = this.readBundle(session.id);
    const mergedPoints = new Map<string, DualVerifyPointJobRecord>();
    for (const p of existing?.points ?? []) mergedPoints.set(p.pointId, p);
    for (const p of points) mergedPoints.set(p.pointId, p);

    const payload: SessionFile = {
      session,
      points: [...mergedPoints.values()].sort((a, b) =>
        a.pointId.localeCompare(b.pointId),
      ),
    };
    writeFileSync(filePath, JSON.stringify(payload, null, 2), 'utf-8');
  }

  readBundle(sessionId: string): SessionFile | null {
    const filePath = join(this.dir, `${sessionId}.json`);
    if (!existsSync(filePath)) return null;
    try {
      return JSON.parse(readFileSync(filePath, 'utf-8')) as SessionFile;
    } catch {
      return null;
    }
  }

  listSessions(limit = 30): DualVerifySessionRecord[] {
    if (!existsSync(this.dir)) return [];
    const files = readdirSync(this.dir)
      .filter((f) => f.endsWith('.json'))
      .map((f) => {
        try {
          const bundle = JSON.parse(
            readFileSync(join(this.dir, f), 'utf-8'),
          ) as SessionFile;
          return bundle.session;
        } catch {
          return null;
        }
      })
      .filter((s): s is DualVerifySessionRecord => s != null);

    return files
      .sort((a, b) => b.updatedAt.localeCompare(a.updatedAt))
      .slice(0, limit);
  }

  hydrateAll(): SessionFile[] {
    if (!existsSync(this.dir)) return [];
    return readdirSync(this.dir)
      .filter((f) => f.endsWith('.json'))
      .map((f) => this.readBundle(f.replace(/\.json$/, '')))
      .filter((b): b is SessionFile => b != null);
  }
}
