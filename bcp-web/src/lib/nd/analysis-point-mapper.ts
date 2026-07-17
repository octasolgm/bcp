import {
  progressPointToReportItem,
  type DualVerifyReportItem,
} from '../dual-verify-report';
import type { DualVerifyAgreement } from '../landing-ai/dual-verify-merge';
import { normalizeSessionPointStatus } from '../session-point-status';
import type { AnalysisPoint } from './types';
import { parsePointSnapshot } from './utils';

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
    errorMessage: p.landingAiError ?? p.googleAiError ?? undefined,
  });
}
