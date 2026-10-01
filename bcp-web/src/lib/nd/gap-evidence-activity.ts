import type { AnalysisPoint, AnalysisRunSummary } from './types';
import { normalizeRegulPoint, regulForwardStatus } from './regul-fields';
import { parsePointSnapshot } from './utils';

export type GapEvidencePrepStep =
  | 'uploading'
  | 'not_started'
  | 'parsing'
  | 'extracting'
  | 'indexing'
  | 'ready'
  | 'failed';

export function gapEvidencePrepStepLabel(
  step: GapEvidencePrepStep,
  fileName?: string,
  detail?: string,
): string {
  const short = fileName?.trim() ? fileName.trim() : 'document';
  const d = (detail ?? '').trim();
  const queued = /^(pending|queued)$/i.test(d);
  switch (step) {
    case 'uploading':
      return `Uploading "${short}"…`;
    case 'not_started':
      return d ? d : `Not yet parsed or extracted`;
    case 'parsing':
      return queued
        ? `Queued for parsing — "${short}"`
        : `Parsing "${short}"…`;
    case 'extracting':
      return `Extracting "${short}"…`;
    case 'indexing':
      return `Indexing "${short}"…`;
    case 'ready':
      return d ? `"${short}" ready for gap re-analysis — ${d}` : `"${short}" ready for gap re-analysis`;
    case 'failed':
      return d ? `Prepare failed for "${short}" — ${d}` : `Prepare failed for "${short}"`;
  }
}

/** True while a single clause worker is actively running (not merely queued as pending). */
export function isPointPipelineRunning(point: AnalysisPoint): boolean {
  const p = normalizeRegulPoint(point);
  const statuses = [regulForwardStatus(p), p.dualVerifyStatus].map((s) => (s ?? '').trim().toLowerCase());
  return statuses.some((s) => s === 'running' || s === 'processing');
}

/** Regul evidence re-run clears finalStatus and sets forward status to pending while re-judging. */
export function isPointGapEvidenceRejudging(point: AnalysisPoint): boolean {
  const p = normalizeRegulPoint(point);
  const s = regulForwardStatus(p).toLowerCase();
  if (s === 'running' || s === 'processing') return true;
  const final = (p.finalStatus ?? '').trim().toLowerCase();
  if (s === 'pending' && !final) return true;
  return false;
}

export function isGapEvidenceRerunInFlight(
  run: Pick<AnalysisRunSummary, 'status' | 'regulPipelinePhase'>,
  points: AnalysisPoint[],
  watchPointIds?: Set<string> | null,
): boolean {
  const runSt = (run.status ?? '').trim().toLowerCase();
  if (runSt === 'running' || runSt === 'processing' || runSt === 'queued') return true;

  const phase = (run.regulPipelinePhase ?? '').trim().toLowerCase();
  if (phase === 'forward' || phase === 'retrieval' || phase === 'parsing') return true;

  const pool =
    watchPointIds && watchPointIds.size > 0
      ? points.filter((p) => p.id && watchPointIds.has(p.id))
      : points;

  return pool.some(isPointGapEvidenceRejudging);
}

function regulEvidencePhaseLabel(phase: string): string {
  switch (phase) {
    case 'parsing':
      return 'Preparing documents';
    case 'retrieval':
      return 'Retrieving relevant policy sections';
    case 'forward':
      return 'Judging clauses against policy';
    default:
      return 'Re-judging open gaps';
  }
}

export function buildGapEvidenceRerunMarquee(
  run: Pick<
    AnalysisRunSummary,
    'status' | 'regulPipelinePhase' | 'regulClauseCompleted' | 'regulClauseTotal' | 'runningPoints'
  >,
  points: AnalysisPoint[],
  watchPointIds?: Set<string> | null,
): string {
  const phase = (run.regulPipelinePhase ?? '').trim().toLowerCase();
  const phaseLabel = regulEvidencePhaseLabel(phase);
  const pool =
    watchPointIds && watchPointIds.size > 0
      ? points.filter((p) => p.id && watchPointIds.has(p.id))
      : points.filter((p) => {
          const fs = (p.finalStatus ?? '').toLowerCase();
          return fs === 'non_compliant' || fs === 'partial_compliant' || isPointGapEvidenceRejudging(p);
        });

  const rejudging = pool.filter(isPointGapEvidenceRejudging);
  const running = pool.filter(isPointPipelineRunning);
  const active = rejudging.length ? rejudging : running;

  const total = watchPointIds?.size ?? pool.length;
  const done = Math.max(0, total - rejudging.length);
  const runSt = (run.status ?? '').trim().toLowerCase();

  if (run.regulClauseTotal != null && run.regulClauseTotal > 0 && (runSt === 'running' || phase)) {
    const completed = run.regulClauseCompleted ?? done;
    const clauseTotal = run.regulClauseTotal;
    return `${phaseLabel} — ${completed}/${clauseTotal} clauses · retrieval · LLM judgment · updating gaps`;
  }

  if (!active.length) {
    if (runSt === 'running' || phase) {
      return `${phaseLabel} — starting workers…`;
    }
    return 'Gap evidence re-analysis starting…';
  }

  const labels = active.map(clauseLabelForPoint);
  const head = labels.slice(0, 4).join(', ');
  const more = labels.length > 4 ? ` (+${labels.length - 4} more)` : '';
  return `${phaseLabel} — ${active.length}/${total || active.length} clause(s): ${head}${more} · retrieval · LLM judgment · updating gaps`;
}

export function clauseLabelForPoint(point: AnalysisPoint): string {
  const snap = parsePointSnapshot(point.pointSnapshot);
  const no = (snap.pointNumber ?? '').replace(/^§/, '').trim();
  return no ? `§${no}` : 'clause';
}

/** UI bucket for hybrid-style rerun progress (All / Running / Queued / Done / Failed). */
export type GapEvidenceRerunUiStatus = 'queued' | 'running' | 'completed' | 'failed';

export function gapEvidenceRerunUiStatus(point: AnalysisPoint): GapEvidenceRerunUiStatus {
  const p = normalizeRegulPoint(point);
  const s = regulForwardStatus(p).toLowerCase();
  if (s === 'failed') return 'failed';
  if (s === 'running' || s === 'processing') return 'running';
  if (isPointGapEvidenceRejudging(p)) return s === 'pending' ? 'queued' : 'running';
  const final = (p.finalStatus ?? '').trim().toLowerCase();
  if (final) return 'completed';
  if (['completed', 'compliant', 'partial_compliant', 'non_compliant'].includes(s)) return 'completed';
  return 'queued';
}

export type GapEvidenceRerunProgressCounts = {
  all: number;
  running: number;
  queued: number;
  failed: number;
  completed: number;
};

export function summarizeGapEvidenceRerunProgress(
  points: AnalysisPoint[],
  watchPointIds: Set<string>,
): GapEvidenceRerunProgressCounts {
  const pool = points.filter((p) => p.id && watchPointIds.has(p.id));
  const counts: GapEvidenceRerunProgressCounts = {
    all: pool.length,
    running: 0,
    queued: 0,
    failed: 0,
    completed: 0,
  };
  for (const p of pool) {
    counts[gapEvidenceRerunUiStatus(p)]++;
  }
  return counts;
}

export type GapEvidenceRerunProgressRow = {
  pointId: string;
  label: string;
  status: GapEvidenceRerunUiStatus;
};

export function gapEvidenceRerunProgressRows(
  points: AnalysisPoint[],
  watchPointIds: Set<string>,
): GapEvidenceRerunProgressRow[] {
  return points
    .filter((p) => p.id && watchPointIds.has(p.id))
    .map((p) => ({
      pointId: p.id!,
      label: clauseLabelForPoint(p),
      status: gapEvidenceRerunUiStatus(p),
    }));
}

export function gapEvidenceRerunUiStatusLabel(status: GapEvidenceRerunUiStatus): string {
  switch (status) {
    case 'running':
      return 'Running';
    case 'queued':
      return 'Queued';
    case 'failed':
      return 'Failed';
    case 'completed':
      return 'Done';
  }
}

export function buildGapEvidencePrepMarquee(labels: Record<string, string>): string | null {
  const active = Object.values(labels).filter(
    (l) =>
      l &&
      !/ready for gap re-analysis/i.test(l) &&
      !/^not yet parsed or extracted$/i.test(l.trim()),
  );
  if (!active.length) return null;
  return active.join(' · ');
}

export function mergeActivityMarquee(prep: string | null, rerun: string | null): string {
  const parts = [prep, rerun].filter((p) => !!p?.trim()) as string[];
  return parts.join(' · ');
}
