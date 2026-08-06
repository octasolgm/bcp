import {
  progressPointToReportItem,
  type DualVerifyReportItem,
} from '../dual-verify-report';
import type { DualVerifyAgreement } from '../landing-ai/dual-verify-merge';
import { normalizeSessionPointStatus } from '../session-point-status';
import { reportItemsToGapItems } from '../../app/services/gap-analysis-mapper';
import type { GapItemData } from '../../app/services/reguliq-store';
import type { AnalysisPoint } from './types';
import { normalizeRegulPoint, regulForwardError, regulForwardStatus, isRegulWorkflow } from './regul-fields';
import { parsePointSnapshot } from './utils';

function comparePointIds(a: string, b: string): number {
  return a.localeCompare(b, undefined, { numeric: true });
}

function parseNdAiPayload(raw?: string | null): {
  message: string;
  agreement?: DualVerifyAgreement;
} {
  if (!raw) return { message: '' };
  try {
    const parsed = JSON.parse(raw) as {
      message?: string;
      agreement?: DualVerifyAgreement;
    };
    return { message: parsed.message ?? '', agreement: parsed.agreement };
  } catch {
    return { message: raw };
  }
}

/** Session/coverage status for a stored ND analysis point (Regul forward/reverse or legacy dual-verify). */
export type RegulCoverageContext = {
  workflowEngine?: string | null;
  regulPipelinePhase?: string | null;
};

export function analysisPointCoverageStatus(
  p: AnalysisPoint,
  runStatus?: string | null,
  regul?: RegulCoverageContext,
): string {
  const point = normalizeRegulPoint(p);
  const landing = parseNdAiPayload(point.landingAiResult);
  const google = parseNdAiPayload(point.googleAiResult);
  const landingMessage = landing.message;
  const llmMessage = google.message;
  const run = (runStatus ?? '').toLowerCase();
  const isRegul = isRegulWorkflow(regul?.workflowEngine);
  const phase = (regul?.regulPipelinePhase ?? '').toLowerCase();
  const runActive = run === 'running' || run === 'processing';
  const forwardStatus = regulForwardStatus(point);

  if (run === 'cancelled') {
    if (point.landingAiStatus === 'cancelled' || point.dualVerifyStatus === 'cancelled') return 'cancelled';
    if (forwardStatus === 'completed') return 'completed';
    if (forwardStatus === 'failed') return 'failed';
    return 'cancelled';
  }

  // Regul V3: forward-complete regulatory rows stay in-flight until pipeline phase is done.
  if (isRegul && point.regulationPointId && runActive && phase !== 'done') {
    if (forwardStatus === 'cancelled') return 'cancelled';
    if (forwardStatus === 'failed') return 'failed';
    if (forwardStatus !== 'completed') return 'running';
    return 'running';
  }

  if (point.landingAiStatus === 'cancelled' || point.dualVerifyStatus === 'cancelled') return 'cancelled';
  if (p.dualVerifyStatus === 'completed' || p.finalStatus) return 'completed';
  if (p.landingAiStatus === 'failed') return 'failed';
  if (
    landingMessage &&
    (p.googleAiStatus === 'failed' || p.dualVerifyStatus === 'failed')
  ) {
    return 'completed';
  }
  if (
    p.landingAiStatus === 'running' ||
    p.dualVerifyStatus === 'running' ||
    p.googleAiStatus === 'running'
  ) {
    return 'running';
  }
  if (p.landingAiStatus === 'completed' && p.dualVerifyStatus !== 'completed') return 'running';
  if (p.landingAiStatus === 'completed') return 'completed';
  if (
    p.landingAiStatus === 'compliant' ||
    p.landingAiStatus === 'partial_compliant' ||
    p.landingAiStatus === 'non_compliant'
  ) {
    return p.dualVerifyStatus === 'passed' ||
      p.dualVerifyStatus === 'failed' ||
      p.dualVerifyStatus === 'skipped'
      ? 'completed'
      : 'running';
  }

  if (run === 'running' || run === 'processing') return 'running';
  return 'not-run';
}

function mapNdPointStatus(
  p: AnalysisPoint,
  landingMessage: string,
  llmMessage: string,
): DualVerifyReportItem['status'] {
  let raw = 'queued';
  if (p.dualVerifyStatus === 'completed' || p.finalStatus) raw = 'completed';
  else if (p.landingAiStatus === 'failed') raw = 'failed';
  else if (
    landingMessage &&
    (p.googleAiStatus === 'failed' || p.dualVerifyStatus === 'failed')
  ) {
    raw = 'completed';
  } else if (
    p.landingAiStatus === 'running' ||
    p.googleAiStatus === 'running' ||
    p.dualVerifyStatus === 'running'
  ) {
    raw = 'running';
  } else if (p.landingAiStatus === 'completed' && p.dualVerifyStatus !== 'completed') {
    raw = 'running';
  } else if (p.landingAiStatus === 'completed') {
    raw = 'completed';
  }

  return normalizeSessionPointStatus({
    status: raw,
    landingMessage,
    llmMessage,
    landingAiStatus: p.landingAiStatus,
    googleAiStatus: p.googleAiStatus,
    dualVerifyStatus: p.dualVerifyStatus,
  }) as DualVerifyReportItem['status'];
}

/** Map an ND analysis point to a dual-verify report row for gap analysis. */
export function analysisPointToReportItem(p: AnalysisPoint): DualVerifyReportItem | null {
  const snap = parsePointSnapshot(p.pointSnapshot);
  const pointId = snap.pointNumber || p.regulationPointId || p.id;
  const landing = parseNdAiPayload(p.landingAiResult);
  const google = parseNdAiPayload(p.googleAiResult);
  const status = mapNdPointStatus(p, landing.message, google.message);

  if (!landing.message && !google.message && status !== 'failed') return null;

  return progressPointToReportItem({
    pointId,
    pointTitle: snap.pointTitle ?? undefined,
    status,
    landingMessage: landing.message || undefined,
    llmMessage: google.message || undefined,
    agreementJson: google.agreement,
    errorMessage: regulForwardError(p) ?? p.googleAiError ?? undefined,
  });
}

/** Map ND analysis points to gap-analysis export rows (Pass 1 only, or Pass 1 + 2). */
export function analysisPointsToGapExportItems(points: AnalysisPoint[]): GapItemData[] {
  const reports: DualVerifyReportItem[] = [];
  const seen = new Set<string>();

  for (const p of points) {
    const report = analysisPointToReportItem(p);
    if (!report) continue;
    const hasOutput = Boolean(report.landingMessage?.trim() || report.llmMessage?.trim());
    if (!hasOutput) continue;
    const snap = parsePointSnapshot(p.pointSnapshot);
    if (snap.pointContent?.trim()) report.govText = snap.pointContent.trim();
    if (seen.has(report.pointId)) continue;
    seen.add(report.pointId);
    reports.push(report);
  }

  reports.sort((a, b) => comparePointIds(a.pointId, b.pointId));
  return reportItemsToGapItems(reports);
}
