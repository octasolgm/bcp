import type { GovPoint } from './gov-point-filter';
import { analyzeGovPointSet, isManualRegulationSource, type GovPointSetAnalysis } from './library-points-utils';
import type { RegulationDocument, RegulationPoint } from './nd/types';
import { isJunkExtractPointId } from './gov-point-filter';
import { sortByPointRef } from './nd/list-utils';

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

export function ndGovPointToRegulationPoint(
  p: Pick<NdGovPoint, 'point_id' | 'pointNumber' | 'title' | 'text' | 'regulationPointId' | 'isIntroductionPoint' | 'isAnnexPoint'>,
): RegulationPoint {
  return {
    id: p.regulationPointId ?? p.point_id,
    pointNumber: p.pointNumber ?? p.point_id,
    pointTitle: p.title ?? null,
    pointContent: p.text ?? '',
    pageReference: null,
    pdfPage: null,
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

/** Prefer API canonical count — frontend display filters must not lower the badge. */
export function resolveRegulationStoredCount(
  points: RegulationPoint[],
  apiPointCount?: number | null,
): number {
  if (apiPointCount != null && apiPointCount > 0) return apiPointCount;
  return filterRegulationPointsForDisplay(points).length;
}

export function computeRegulationPointStats(
  points: RegulationPoint[],
  docName?: string | null,
  apiPointCount?: number | null,
): GovPointSetAnalysis {
  const filtered = filterRegulationPointsForDisplay(points);
  const analyzed = analyzeGovPointSet(
    filtered.map((p) =>
      regulationPointToGovPoint(p, docName ? { manual: isManualRegulationSource(docName) } : undefined),
    ),
    { docName },
  );
  const storedCount = resolveRegulationStoredCount(filtered, apiPointCount);
  const analyseCount = Math.min(analyzed.analyseCount, storedCount);
  return {
    ...analyzed,
    storedCount,
    analyseCount,
    skippedCount: Math.max(0, storedCount - analyseCount),
  };
}

/** Normalize a points API payload to display-safe rows + authoritative stored count. */
export function prepareRegulationPointsResponse(
  raw: unknown[],
  options?: { docName?: string | null; apiPointCount?: number | null },
): { points: RegulationPoint[]; storedCount: number; stats: GovPointSetAnalysis } {
  const normalized = raw
    .map((row) => normalizeRegulationPoint(row as Record<string, unknown>))
    .filter((p) => (p.pointNumber ?? '').trim());
  const points = filterRegulationPointsForDisplay(normalized);
  const stats = computeRegulationPointStats(points, options?.docName, options?.apiPointCount);
  return { points, storedCount: stats.storedCount, stats };
}

export function regulationDocPointLabel(
  stored: number | null | undefined,
): string {
  return `${stored ?? 0} pts`;
}

/** Empty manuals are a placeholder until the first point exists — they do not count as a regulation document. */
export function regulationDocumentCountsTowardTotal(doc: {
  isManual?: boolean;
  source?: string;
  isNdManual?: boolean;
  pointCount?: number | null;
}): boolean {
  const isManual = doc.isManual === true || doc.source === 'manual' || doc.isNdManual === true;
  if (!isManual) return true;
  return (doc.pointCount ?? 0) > 0;
}

/** When the API returns both a legacy stored-doc row and an nd row, keep the richer nd card. */
export function dedupeRegulationDocuments(docs: RegulationDocument[]): RegulationDocument[] {
  const manualOrUnstored: RegulationDocument[] = [];
  const byStored = new Map<string, RegulationDocument>();

  for (const doc of docs) {
    if (doc.isManual || doc.source === 'manual' || !doc.storedDocumentId) {
      manualOrUnstored.push(doc);
      continue;
    }
    const key = doc.storedDocumentId;
    const existing = byStored.get(key);
    if (!existing || regulationDocumentRowRank(doc) > regulationDocumentRowRank(existing)) {
      byStored.set(key, doc);
    }
  }

  return [...manualOrUnstored, ...byStored.values()];
}

function regulationDocumentRowRank(doc: RegulationDocument): number {
  let rank = 0;
  if (doc.source === 'nd') rank += 8;
  const pts = doc.pointCount ?? 0;
  if (pts > 0) rank += 4;
  const st = (doc.extractionStatus ?? '').toLowerCase();
  if (st === 'extracted' || st === 'completed') rank += 2;
  if (st === 'processing') rank += 1;
  return rank;
}

export function sortRegulationDocuments(docs: RegulationDocument[]): RegulationDocument[] {
  return [...docs].sort((a, b) => {
    const am = a.isManual || a.source === 'manual' ? 0 : 1;
    const bm = b.isManual || b.source === 'manual' ? 0 : 1;
    if (am !== bm) return am - bm;
    return (b.createdAt ?? '').localeCompare(a.createdAt ?? '');
  });
}

export function findRegulationDocumentIndex(
  docs: RegulationDocument[],
  id: string,
): number {
  return docs.findIndex((d) => d.id === id || d.storedDocumentId === id);
}

export function regulationDocLookupIds(doc: RegulationDocument): string[] {
  return [doc.id, doc.storedDocumentId].filter((v): v is string => !!v);
}

export function isRegulationExtractTerminal(status: string | null | undefined): boolean {
  const st = (status ?? '').toLowerCase();
  return st === 'extracted' || st === 'completed' || st === 'failed' || st === 'paused';
}

export function isRegulationExtractSuccess(
  status: string | null | undefined,
  pointCount: number,
): boolean {
  const st = (status ?? '').toLowerCase();
  return (st === 'extracted' || st === 'completed') && pointCount > 0;
}

/** Drop junk extract ids and collapse exact duplicate rows (same number + near-identical content). */
export function filterRegulationPointsForDisplay(points: RegulationPoint[]): RegulationPoint[] {
  const survivors = new Map<string, RegulationPoint>();
  for (const point of points) {
    const number = (point.pointNumber ?? '').trim();
    if (!number || isJunkExtractPointId(number)) continue;

    const key = number.toLowerCase();
    const existing = survivors.get(key);
    if (!existing) {
      survivors.set(key, point);
      continue;
    }

    const existingLen = (existing.pointContent ?? '').trim().length;
    const candidateLen = (point.pointContent ?? '').trim().length;
    const sameContent =
      (existing.pointContent ?? '').trim().toLowerCase() === (point.pointContent ?? '').trim().toLowerCase();
    if (sameContent) continue;
    if (candidateLen > existingLen) survivors.set(key, point);
  }

  return sortByPointRef([...survivors.values()], (p) => p.pointNumber ?? '');
}
