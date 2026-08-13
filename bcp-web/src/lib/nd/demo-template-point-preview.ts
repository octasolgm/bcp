import type { AnalysisPoint, PointSnapshot, RegulationPoint } from './types';

export type DemoTemplatePointLike = {
  id: string;
  clauseNo: string;
  clauseTitle?: string | null;
  designStatus: string;
  operatingStatus: string;
  overallStatus: string;
  confidence: number;
  interpretation: string;
  policyExtract: string[];
  documentReference: string;
  gapDescription: string;
  suggestedAction: string;
  gapDirection: string;
};

export function normalizeClauseKey(clauseNo: string): string {
  return clauseNo.trim().replace(/^§/, '');
}

export function findRegulationPointForClause(
  points: RegulationPoint[],
  clauseNo: string,
): RegulationPoint | undefined {
  const key = normalizeClauseKey(clauseNo);
  if (!key) return undefined;
  return points.find((p) => normalizeClauseKey(p.pointNumber) === key);
}

export function mapDemoDisplayStatus(overallStatus?: string, designStatus?: string): string {
  let s = (overallStatus ?? '').trim().toLowerCase();
  if (!s && designStatus?.trim()) s = designStatus.trim().toLowerCase();
  if (s === 'compliant') return 'Compliant';
  if (s.includes('partial')) return 'Partial compliant';
  if (s.includes('non')) return 'Non-Compliant';
  return s ? overallStatus!.trim() : 'Non-Compliant';
}

export function mapDemoFinalStatus(overallStatus?: string, designStatus?: string): string {
  const display = mapDemoDisplayStatus(overallStatus, designStatus).toLowerCase();
  if (display === 'compliant') return 'compliant';
  if (display.includes('partial')) return 'partial_compliant';
  return 'non_compliant';
}

function buildDemoPolicyResponse(point: DemoTemplatePointLike): string {
  const extracts = (point.policyExtract ?? []).filter((s) => s?.trim());
  if (extracts.length === 1) return extracts[0].trim();
  if (extracts.length > 1) {
    return extracts.map((s, i) => `(${i + 1}) ${s.trim()}`).join('\n\n');
  }
  if (point.documentReference?.trim()) return `See ${point.documentReference.trim()}.`;
  return 'No corresponding procedure found.';
}

function buildDemoGapAnalysisText(point: DemoTemplatePointLike): string {
  const status = mapDemoDisplayStatus(point.overallStatus, point.designStatus);
  if (status === 'Compliant') return '';

  const explicit = point.gapDescription?.trim();
  if (explicit) return explicit;

  const fromInterpretation = point.interpretation?.trim();
  if (fromInterpretation) return fromInterpretation;

  return '';
}

/** Mirrors backend NdRegulJudgmentFormatter.FormatLandingMessage for admin preview. */
export function formatDemoJudgmentLandingMessage(
  point: DemoTemplatePointLike,
  clauseText?: string,
): string {
  const status = mapDemoDisplayStatus(point.overallStatus, point.designStatus);
  const confidencePct = Math.round(Math.max(0, Math.min(1, point.confidence ?? 0)) * 100);
  const policyResponse = buildDemoPolicyResponse(point);
  const fulfilled = status === 'Compliant' ? 'All required elements addressed.' : 'None';
  const gapAnalysis = buildDemoGapAnalysisText(point);

  let corrective = point.suggestedAction?.trim() || '';
  if (!corrective) corrective = status === 'Compliant' ? 'N/A' : '—';

  const reference = point.documentReference?.trim() || 'Internal policy manual';
  const clauseBody = clauseText?.trim() || '';

  return [
    point.clauseNo,
    clauseBody,
    '',
    'Reference PDF :',
    reference,
    '',
    'Document Reference :',
    reference,
    '',
    'Output/Response :',
    policyResponse,
    '',
    'Fulfilled clauses :',
    fulfilled,
    '',
    `Comply Yes/No (Status) : ${status}`,
    `Compliance Confidence % : ${confidencePct}%`,
    'Gap analysis :',
    gapAnalysis ? gapAnalysis : status === 'Compliant' ? 'N/A' : '—',
    'Corrective Action Plan :',
    corrective,
    'Responsibility :',
    status === 'Compliant' ? 'N/A' : 'Compliance / policy owner',
  ].join('\n');
}

export function demoTemplatePointToPreview(
  point: DemoTemplatePointLike,
  regPoint?: RegulationPoint | null,
): { analysisPoint: AnalysisPoint; snapshot: PointSnapshot } {
  const clauseText = regPoint?.pointContent?.trim() || '';
  const landingMessage = formatDemoJudgmentLandingMessage(point, clauseText);
  const finalStatus = mapDemoFinalStatus(point.overallStatus, point.designStatus);
  const cap = point.suggestedAction?.trim() || '';

  const snapshot: PointSnapshot = {
    pointNumber: point.clauseNo,
    pointTitle: point.clauseTitle ?? regPoint?.pointTitle ?? undefined,
    pointContent: clauseText || point.interpretation?.trim() || '',
    pageReference: regPoint?.pageReference ?? undefined,
    pdfPage: regPoint?.pdfPage ?? undefined,
    regulationPointId: regPoint?.id,
  };

  const analysisPoint: AnalysisPoint = {
    id: point.id,
    regulationPointId: regPoint?.id ?? null,
    pointSnapshot: JSON.stringify(snapshot),
    landingAiStatus: 'completed',
    landingAiResult: JSON.stringify({ message: landingMessage }),
    googleAiStatus: 'skipped',
    dualVerifyStatus: 'completed',
    finalStatus,
    finalActionPlan: cap || null,
    originalAiActionPlan: cap || null,
  };

  return { analysisPoint, snapshot };
}
