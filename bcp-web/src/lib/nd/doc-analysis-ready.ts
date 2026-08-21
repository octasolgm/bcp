import type { ActionPlanPriority } from './action-plan';

/** Readiness of a regulation or internal document for a new analysis run. */
export type DocAnalysisReadyState = 'not_ready' | 'ready' | 'analysed';

export function regulationAnalysisReadyState(doc: {
  isManual?: boolean | null;
  isNdManual?: boolean | null;
  extractionStatus?: string | null;
  pointCount?: number | null;
  analysisRunCount?: number | null;
}): DocAnalysisReadyState {
  const analysed = (doc.analysisRunCount ?? 0) > 0;
  const isManual = doc.isManual === true || doc.isNdManual === true;
  const status = (doc.extractionStatus ?? '').trim();
  const hasPoints = (doc.pointCount ?? 0) > 0;
  // A blank status means the caller has no extraction metadata (legacy list),
  // so points alone decide readiness.
  const extracted =
    hasPoints &&
    (isManual || status === '' || /^(extracted|completed|manual)$/i.test(status));
  if (!extracted) return 'not_ready';
  return analysed ? 'analysed' : 'ready';
}

export function internalAnalysisReadyState(doc: {
  parseStatus?: string | null;
  sectionExtractStatus?: string | null;
  sectionCount?: number | null;
  analysisRunCount?: number | null;
}): DocAnalysisReadyState {
  const parseStatus = (doc.parseStatus ?? '').trim();
  const extractStatus = (doc.sectionExtractStatus ?? '').trim();
  // Blank means the caller has no metadata for that step, which must not be
  // read as a failure or legacy lists would become entirely unselectable.
  const parsed = parseStatus === '' || /^(parsed|completed)$/i.test(parseStatus);
  const extracted =
    extractStatus === '' ||
    /^(extracted|completed)$/i.test(extractStatus) ||
    (doc.sectionCount ?? 0) > 0;
  if (!parsed || !extracted) return 'not_ready';
  return (doc.analysisRunCount ?? 0) > 0 ? 'analysed' : 'ready';
}

export function docAnalysisReadyLabel(state: DocAnalysisReadyState): string {
  if (state === 'not_ready') return 'Not parsed / not extracted';
  if (state === 'ready') return 'Ready to analyse';
  return 'Already analysed';
}

export function docAnalysisReadyClass(state: DocAnalysisReadyState): string {
  if (state === 'not_ready') return 'doc-ready-red';
  if (state === 'ready') return 'doc-ready-blue';
  return 'doc-ready-green';
}

export function usedInAnalysesLabel(count: number | null | undefined): string {
  const n = count ?? 0;
  return n === 1 ? 'Used in 1 analysis' : `Used in ${n} analyses`;
}

/** Target date offset from gap risk: High 15 · Medium 30 · Low 45 days. */
export const GAP_RISK_DUE_DAYS: Record<ActionPlanPriority, number> = {
  high: 15,
  medium: 30,
  low: 45,
};

export function defaultTargetDateForGapRisk(
  risk: ActionPlanPriority,
  from: Date = new Date(),
): string {
  const days = GAP_RISK_DUE_DAYS[risk];
  const d = new Date(from.getTime());
  d.setDate(d.getDate() + days);
  const y = d.getFullYear();
  const m = String(d.getMonth() + 1).padStart(2, '0');
  const day = String(d.getDate()).padStart(2, '0');
  return `${y}-${m}-${day}`;
}

/** CAP gaps write "higher"/"lower" rather than the high/low the UI works in. */
export function normalizeGapRisk(raw: string | null | undefined): ActionPlanPriority {
  const v = (raw ?? '').trim().toLowerCase();
  if (v === 'high' || v === 'higher' || v === 'critical') return 'high';
  if (v === 'low' || v === 'lower') return 'low';
  return 'medium';
}
