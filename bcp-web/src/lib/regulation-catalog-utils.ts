import type { GovPoint } from './gov-point-filter';
import { analyzeGovPointSet, isManualRegulationSource, type GovPointSetAnalysis } from './library-points-utils';
import type { RegulationDocument, RegulationPoint } from './nd/types';

export function normalizeRegulationPoint(raw: Record<string, unknown>): RegulationPoint {
  return {
    id: String(raw['id'] ?? ''),
    pointNumber: String(raw['pointNumber'] ?? raw['point_number'] ?? ''),
    pointTitle: (raw['pointTitle'] ?? raw['point_title'] ?? null) as string | null,
    pointContent: String(raw['pointContent'] ?? raw['point_content'] ?? ''),
    pageReference: (raw['pageReference'] ?? raw['page_reference'] ?? null) as string | null,
    isIntroductionPoint: Boolean(raw['isIntroductionPoint'] ?? raw['is_introduction_point']),
    isAnnexPoint: Boolean(raw['isAnnexPoint'] ?? raw['is_annex_point']),
  };
}

export function formatRegulationPointLabel(
  p: Pick<RegulationPoint, 'pointNumber' | 'pointTitle'>,
): string {
  const num = p.pointNumber.trim();
  const title = (p.pointTitle ?? '').trim();
  return title ? `${num} — ${title}` : num;
}

export function manualRegulationPointToGovPoint(p: RegulationPoint): GovPoint {
  return {
    point_id: p.pointNumber,
    title: p.pointTitle ?? undefined,
    text: p.pointContent,
  };
}

export function regulationPointToGovPoint(
  p: RegulationPoint,
  options?: { manual?: boolean },
): GovPoint {
  if (options?.manual) return manualRegulationPointToGovPoint(p);
  return {
    point_id: p.pointNumber,
    title: p.pointTitle ?? undefined,
    text: p.pointContent,
    section: p.pageReference ?? undefined,
  };
}

export type NdGovPoint = GovPoint & {
  regulationPointId: string;
  regulationDocumentId: string;
  pointNumber: string;
  docId: string;
  docName: string;
};

export function regulationPointToNdGovPoint(
  p: RegulationPoint,
  meta: { docId: string; docName: string; isManual?: boolean },
): NdGovPoint {
  const base = regulationPointToGovPoint(p, { manual: meta.isManual });
  return {
    ...base,
    point_id: p.id || `${meta.docId}:${p.pointNumber}`,
    pointNumber: p.pointNumber,
    regulationPointId: p.id,
    regulationDocumentId: meta.docId,
    docName: meta.docName,
    docId: meta.docId,
  };
}

export function govPointForGrouping(p: Pick<NdGovPoint, 'pointNumber' | 'point_id' | 'title' | 'text' | 'section'>): GovPoint {
  return {
    point_id: p.pointNumber || p.point_id,
    title: p.title,
    text: p.text,
    section: p.section,
  };
}
export function computeRegulationPointStats(
  points: RegulationPoint[],
  docName?: string | null,
): GovPointSetAnalysis {
  return analyzeGovPointSet(
    points.map((p) => regulationPointToGovPoint(p, docName ? { manual: isManualRegulationSource(docName) } : undefined)),
    { docName },
  );
}

export function regulationDocPointLabel(
  stored: number | null | undefined,
): string {
  return `${stored ?? 0} pts`;
}

/** One row per underlying file — prefer ND upload with more points over legacy stub. Manual docs are never merged. */
export function dedupeRegulationDocuments(docs: RegulationDocument[]): RegulationDocument[] {
  const byKey = new Map<string, RegulationDocument>();
  for (const doc of docs) {
    if (doc.isManual || doc.source === 'manual') {
      byKey.set(`manual:${doc.id}`, doc);
      continue;
    }
    const key = doc.storedDocumentId ?? doc.id;
    const existing = byKey.get(key);
    if (!existing || preferRegulationDocument(doc, existing)) {
      byKey.set(key, doc);
    }
  }
  return [...byKey.values()];
}

function preferRegulationDocument(candidate: RegulationDocument, current: RegulationDocument): boolean {
  if (candidate.source === 'nd' && current.source !== 'nd') return true;
  if (candidate.source !== 'nd' && current.source === 'nd') return false;
  return (candidate.pointCount ?? 0) > (current.pointCount ?? 0);
}

export function sortRegulationDocuments(docs: RegulationDocument[]): RegulationDocument[] {
  return [...docs].sort((a, b) => {
    const am = a.isManual || a.source === 'manual' ? 0 : 1;
    const bm = b.isManual || b.source === 'manual' ? 0 : 1;
    if (am !== bm) return am - bm;
    return a.name.localeCompare(b.name);
  });
}
