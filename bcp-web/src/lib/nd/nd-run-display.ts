import type { AnalysisPoint } from './types';
import { parsePointSnapshot } from './utils';
import { resolveAnalysisPointSeverity } from './point-compliance-status';
import {
  normalizeGapSeverity,
  type GapDraftOverlay,
  type GapItemData,
} from '../../app/services/reguliq-store';

export type NdComplianceSummary = {
  compliant: number;
  partialCompliant: number;
  nonCompliant: number;
};

/** Same compliance counts as gap-analysis summary cards — from persisted run points. */
export function ndComplianceSummaryFromPoints(points: AnalysisPoint[]): NdComplianceSummary {
  let compliant = 0;
  let partialCompliant = 0;
  let nonCompliant = 0;
  for (const point of points) {
    const severity = resolveAnalysisPointSeverity(point);
    if (!severity) continue;
    if (severity === 'compliant') compliant++;
    else if (severity === 'partial_compliant') partialCompliant++;
    else nonCompliant++;
  }
  return { compliant, partialCompliant, nonCompliant };
}

function pointHasSavedOutput(point: AnalysisPoint): boolean {
  return (
    Boolean(point.landingAiResult?.trim()) ||
    Boolean(point.googleAiResult?.trim()) ||
    Boolean(point.finalStatus?.trim()) ||
    Boolean(point.finalActionPlan?.trim()) ||
    Boolean(point.originalAiActionPlan?.trim())
  );
}

/**
 * Build gap-analysis list rows from ND run points (single source for gap report + embedded v8).
 * Severity uses resolveAnalysisPointSeverity (finalStatus + agreement + structured AI fields).
 */
export function buildNdGapListItems(
  points: AnalysisPoint[],
  overlays: Record<string, GapDraftOverlay> = {},
): { items: GapItemData[]; pointIds: string[] } {
  const items: GapItemData[] = [];
  const pointIds: string[] = [];
  let displayIndex = 0;

  for (const point of points) {
    const snap = parsePointSnapshot(point.pointSnapshot);
    const pointKey = (snap.pointNumber || point.regulationPointId || point.id || '').trim();
    if (!pointKey || !pointHasSavedOutput(point)) continue;

    const severity = resolveAnalysisPointSeverity(point);
    if (!severity) continue;

    displayIndex++;
    const sectionLabel = pointKey.startsWith('§') ? pointKey : `§${pointKey}`;
    const overlayKey = sectionLabel.replace(/^§/, '');
    const overlay = overlays[overlayKey];

    pointIds.push(pointKey);
    items.push({
      id: String(displayIndex).padStart(2, '0'),
      pointId: point.id,
      section: sectionLabel,
      title: snap.pointTitle?.trim() || pointKey,
      severity: normalizeGapSeverity(severity),
      gapCount: 0,
      signedOff: overlay?.signedOff ?? false,
      expanded: false,
      regulatoryText: snap.pointContent?.trim() || snap.pointTitle?.trim() || pointKey,
      policyText: overlay?.gaps ? '' : '(See detail panel)',
      regPage: 'source',
      policyPage: 'source',
      gaps: overlay?.gaps ?? '',
      managementResponse: overlay?.managementResponse ?? '',
      designEffectiveness: overlay?.designEffectiveness ?? '',
      operatingEffectiveness: overlay?.operatingEffectiveness ?? '',
      overallEffectiveness: overlay?.overallEffectiveness ?? '',
      documentReference: overlay?.documentReference ?? '',
      evidence: overlay?.evidence ?? '',
    });
  }

  return { items, pointIds };
}
