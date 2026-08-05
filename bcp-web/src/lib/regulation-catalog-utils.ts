import type { GovPoint } from './gov-point-filter';
import { analyzeGovPointSet, isManualRegulationSource, type GovPointSetAnalysis } from './library-points-utils';
import type { RegulationDocument, RegulationPoint } from './nd/types';

function extractSectionFromPageReference(pageReference?: string | null): string | undefined {
  const ref = (pageReference ?? '').trim();
  if (!ref) return undefined;
  const pageSplit = ref.search(/\s*·\s*p\.\s*\d+/i);
  if (pageSplit > 0) return ref.slice(0, pageSplit).trim();
  if (/^p\.\s*\d+$/i.test(ref)) return undefined;
  return ref;
}

export function normalizeRegulationPoint(raw: Record<string, unknown>): RegulationPoint {
  return {
    id: String(raw['id'] ?? ''),
    pointNumber: String(raw['pointNumber'] ?? raw['point_number'] ?? ''),
    pointTitle: (raw['pointTitle'] ?? raw['point_title'] ?? null) as string | null,
    pointContent: String(raw['pointContent'] ?? raw['point_content'] ?? ''),
    pageReference: (raw['pageReference'] ?? raw['page_reference'] ?? null) as string | null,
    pdfPage: (raw['pdfPage'] ?? raw['pdf_page'] ?? raw['pageHint'] ?? raw['page_hint'] ?? null) as
      | number
      | null,
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
  const pageRef = (p.pageReference ?? '').trim();
  const sectionFromRef = extractSectionFromPageReference(pageRef);
  return {
    point_id: p.pointNumber,
    title: p.pointTitle ?? undefined,
    text: p.pointContent,
    section: sectionFromRef ?? (pageRef || undefined),
  };
}

export type NdGovPoint = GovPoint & {
  regulationPointId: string;
  regulationDocumentId: string;
  pointNumber: string;
  docId: string;
  docName: string;
  isIntroductionPoint?: boolean;
  isAnnexPoint?: boolean;
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
    isIntroductionPoint: p.isIntroductionPoint,
    isAnnexPoint: p.isAnnexPoint,
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

/** Keep every upload as its own row (no merge by stored file id). Manual docs are unchanged. */
export function dedupeRegulationDocuments(docs: RegulationDocument[]): RegulationDocument[] {
  return docs;
}

export function sortRegulationDocuments(docs: RegulationDocument[]): RegulationDocument[] {
  return [...docs].sort((a, b) => {
    const am = a.isManual || a.source === 'manual' ? 0 : 1;
    const bm = b.isManual || b.source === 'manual' ? 0 : 1;
    if (am !== bm) return am - bm;
    return (b.createdAt ?? '').localeCompare(a.createdAt ?? '');
  });
}
