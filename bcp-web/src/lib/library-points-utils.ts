import {
  classifyGovPoint,
  enrichGovPointSectionsForPicker,
  filterComparableGovLeafPoints,
  formatChapterLabel,
  formatGovPointDisplayId,
  formatSectionGroupLabel,
  groupGovPointsByChapter,
  groupGovPointsForPicker,
  isSectionOnePoint,
  isJunkExtractPointId,
  resolveGovPointDisplayNumber,
  resolveLogicalPointId,
  normalizeNumericPointId,
  sectionHeadingTitleForKey,
  type GovPoint,
  type GovPointChapterGroup,
} from './gov-point-filter';
import { manualRegulationPointToGovPoint } from './regulation-catalog-utils';

export type SourcedGovPoint = GovPoint & {
  sourceLabel?: string;
  sourceKey?: string;
  regulationPointId?: string;
  libraryId?: string;
  libraryName?: string;
  docId?: string;
  docName?: string;
  pointNumber?: string;
  isIntroductionPoint?: boolean;
  isAnnexPoint?: boolean;
};

export type LibraryDocGroup = {
  key: string;
  docId: string;
  docName: string;
  storedCount: number;
  points: SourcedGovPoint[];
};

export type LibraryHierarchyGroup = {
  key: string;
  libraryId: string;
  libraryName: string;
  storedCount: number;
  analyseCount: number;
  documents: LibraryDocGroup[];
};

export type GovPointDuplicateGroup = {
  key: string;
  displayId: string;
  primary: SourcedGovPoint;
  duplicates: SourcedGovPoint[];
};

export type LibrarySourceCategory = {
  key: string;
  label: string;
  points: SourcedGovPoint[];
};

export function normalizePointNumber(pointId: string): string {
  return pointId.replace(/^§\s*/, '').trim().toLowerCase();
}

/** Fingerprint for cross-document duplicate detection in library builder. */
export function libraryPointFingerprint(
  pointNumber: string,
  title?: string | null,
  content?: string,
): string {
  const n = normalizePointNumber(pointNumber);
  const t = (title ?? '').trim().toLowerCase();
  const c = (content ?? '').trim().toLowerCase().replace(/\s+/g, ' ').slice(0, 300);
  return `${n}|${t}|${c}`;
}

export function fingerprintFromSnapshot(snapshot: Record<string, unknown>): string {
  return libraryPointFingerprint(
    String(snapshot['pointNumber'] ?? ''),
    snapshot['pointTitle'] as string | null | undefined,
    String(snapshot['pointContent'] ?? ''),
  );
}

export function dedupeGovPointsByFingerprint<T extends GovPoint>(
  points: T[],
): { unique: T[]; duplicateGroups: GovPointDuplicateGroup[] } {
  const byKey = new Map<string, T[]>();
  for (const p of points) {
    const key = libraryPointFingerprint(p.point_id, p.title, p.text);
    const list = byKey.get(key) ?? [];
    list.push(p);
    byKey.set(key, list);
  }

  const unique: T[] = [];
  const duplicateGroups: GovPointDuplicateGroup[] = [];

  for (const [key, group] of byKey) {
    group.sort((a, b) => a.point_id.localeCompare(b.point_id, undefined, { numeric: true }));
    unique.push(group[0]);
    if (group.length > 1) {
      duplicateGroups.push({
        key,
        displayId: group[0].section?.trim() || group[0].point_id,
        primary: group[0] as SourcedGovPoint,
        duplicates: group.slice(1) as SourcedGovPoint[],
      });
    }
  }

  unique.sort((a, b) => {
    const aId = a.section?.trim() || a.point_id;
    const bId = b.section?.trim() || b.point_id;
    return aId.localeCompare(bId, undefined, { numeric: true });
  });
  duplicateGroups.sort((a, b) =>
    a.displayId.localeCompare(b.displayId, undefined, { numeric: true }),
  );
  return { unique, duplicateGroups };
}

/** @deprecated Use dedupeGovPointsByFingerprint — number-only merge hid distinct cross-doc points. */
export function dedupeGovPointsByNumber<T extends GovPoint>(
  points: T[],
): { unique: T[]; duplicateGroups: GovPointDuplicateGroup[] } {
  return dedupeGovPointsByFingerprint(points);
}

function isUuidLike(value: string): boolean {
  return /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value.trim());
}

/** Keep numeric clause ids (3.1.5) as selection keys; link DB row via regulationPointId. */
export function assignUniqueLibraryPointIds<T extends SourcedGovPoint>(points: T[]): T[] {
  return points.map((p) => {
    const idRaw = p.point_id.trim().replace(/\.$/, '');
    const idClause = !isUuidLike(idRaw) ? normalizeNumericPointId(idRaw) : null;
    const numRaw = (p.pointNumber ?? '').trim().replace(/\.$/, '');
    const numClause =
      numRaw && !isUuidLike(numRaw) ? normalizeNumericPointId(numRaw) : null;
    let clauseId = idClause;
    if (idClause && numClause) {
      const idDepth = idClause.split('.').length;
      const numDepth = numClause.split('.').length;
      clauseId = idDepth >= numDepth ? idClause : numClause;
    } else {
      clauseId =
        idClause ||
        numClause ||
        (() => {
          const logical = resolveLogicalPointId(p);
          return (
            normalizeNumericPointId(logical) ||
            (logical && !isUuidLike(logical) && !isJunkExtractPointId(logical)
              ? logical
              : '')
          );
        })();
    }
    const regId = p.regulationPointId?.trim() || '';
    const selectionId = clauseId || regId || idRaw;
    return {
      ...p,
      pointNumber: clauseId || p.pointNumber,
      point_id: selectionId,
      regulationPointId: regId || p.regulationPointId,
    };
  });
}

export function groupSourcedGovPointsByCategory(points: SourcedGovPoint[]): LibrarySourceCategory[] {
  const byKey = new Map<string, LibrarySourceCategory>();
  for (const p of points) {
    const key = p.sourceKey ?? p.sourceLabel ?? 'default';
    const label = p.docName ?? p.sourceLabel ?? 'Regulation points';
    const existing = byKey.get(key);
    if (existing) {
      existing.points.push(p);
    } else {
      byKey.set(key, { key, label, points: [p] });
    }
  }
  return [...byKey.values()].sort((a, b) => a.label.localeCompare(b.label));
}

function parseSourceKey(point: SourcedGovPoint): { libraryId: string; docId: string } {
  if (point.libraryId && point.docId) {
    return { libraryId: point.libraryId, docId: point.docId };
  }
  const key = point.sourceKey ?? '';
  const sep = key.indexOf(':');
  if (sep > 0) {
    return { libraryId: key.slice(0, sep), docId: key.slice(sep + 1) };
  }
  return { libraryId: key || 'library', docId: point.docId ?? 'doc' };
}

export type LibraryPointDisplayRow = {
  point: SourcedGovPoint;
  displayId: string;
  forAnalysis: boolean;
  isIntroduction: boolean;
  depth: number;
};

export type PointDisplayTreeNode = {
  key: string;
  displayId: string;
  row?: LibraryPointDisplayRow;
  children: PointDisplayTreeNode[];
  depth: number;
  storedCount: number;
  analyseCount: number;
};

export type LibraryPointDisplaySection = {
  key: string;
  label: string;
  rows: LibraryPointDisplayRow[];
};

export type LibraryPointDisplayChapter = {
  chapter: string;
  label: string;
  sections: LibraryPointDisplaySection[];
  tree: PointDisplayTreeNode[];
  storedCount: number;
  analyseCount: number;
};

export type LibraryPointDisplayDoc = {
  key: string;
  docId: string;
  docName: string;
  chapters: LibraryPointDisplayChapter[];
  flatRows: LibraryPointDisplayRow[];
  pointTree: PointDisplayTreeNode[];
  useChapters: boolean;
  storedCount: number;
  analyseCount: number;
};

export type LibraryPointDisplayTree = {
  key: string;
  libraryId: string;
  libraryName: string;
  documents: LibraryPointDisplayDoc[];
  storedCount: number;
  analyseCount: number;
};

function pointNumberDepth(pointId: string): number {
  const id = pointId.trim().replace(/\.$/, '');
  if (!id || !/^\d+(?:\.\d+)*$/.test(id)) return 1;
  return id.split('.').length;
}

function sortTreeNodes(nodes: PointDisplayTreeNode[]): PointDisplayTreeNode[] {
  return [...nodes]
    .sort((a, b) => a.displayId.localeCompare(b.displayId, undefined, { numeric: true }))
    .map((n) => ({ ...n, children: sortTreeNodes(n.children) }));
}

/** Nest rows as 2 → 2.1 → 2.1.1 (creates parent nodes when only leaves are stored). */
export function buildPointNumberTree(rows: LibraryPointDisplayRow[]): PointDisplayTreeNode[] {
  const roots: PointDisplayTreeNode[] = [];

  const ensureChild = (
    siblings: PointDisplayTreeNode[],
    path: string,
  ): PointDisplayTreeNode => {
    let node = siblings.find((n) => n.key === path);
    if (!node) {
      node = {
        key: path,
        displayId: path,
        children: [],
        depth: path.split('.').length,
        storedCount: 0,
        analyseCount: 0,
      };
      siblings.push(node);
    }
    return node;
  };

  for (const row of sortDisplayRows(rows)) {
    const id = row.displayId.trim().replace(/\.$/, '');
    if (!/^\d+(?:\.\d+)*$/.test(id)) {
      roots.push({
        key: id,
        displayId: id,
        row,
        children: [],
        depth: 1,
        storedCount: 1,
        analyseCount: row.forAnalysis ? 1 : 0,
      });
      continue;
    }

    const parts = id.split('.');
    let siblings = roots;
    let path = '';
    for (let i = 0; i < parts.length; i++) {
      path = i === 0 ? parts[0] : `${path}.${parts[i]}`;
      const node = ensureChild(siblings, path);
      node.storedCount += 1;
      if (row.forAnalysis) node.analyseCount += 1;
      if (i === parts.length - 1) node.row = row;
      siblings = node.children;
    }
  }

  return sortTreeNodes(roots);
}

/** Drop redundant root when chapter accordion already shows §N (avoids §2 → §2 nesting). */
function unwrapChapterRedundantRoot(
  chapter: string,
  tree: PointDisplayTreeNode[],
): PointDisplayTreeNode[] {
  const norm = chapter.trim().replace(/\.$/, '');
  if (!norm || norm === 'other' || tree.length !== 1) return tree;
  const root = tree[0];
  const rootId = root.displayId.trim().replace(/\.$/, '');
  if (rootId !== norm || root.children.length === 0) return tree;
  return root.children;
}

export function chapterTreeFromRows(
  chapter: string,
  rows: LibraryPointDisplayRow[],
): PointDisplayTreeNode[] {
  return unwrapChapterRedundantRoot(chapter, buildPointNumberTree(rows));
}

function attachChapterTrees(chapters: LibraryPointDisplayChapter[]): LibraryPointDisplayChapter[] {
  return chapters.map((ch) => {
    const allRows = ch.sections.flatMap((sec) => sec.rows);
    return {
      ...ch,
      tree: chapterTreeFromRows(ch.chapter, allRows),
    };
  });
}

function sortDisplayRows(rows: LibraryPointDisplayRow[]): LibraryPointDisplayRow[] {
  return [...rows].sort((a, b) =>
    a.displayId.localeCompare(b.displayId, undefined, { numeric: true }),
  );
}

function sourcedToGovPoint(p: SourcedGovPoint): GovPoint {
  if (isManualRegulationSource(p.docName)) {
    return manualRegulationPointToGovPoint({
      id: p.regulationPointId ?? p.point_id,
      pointNumber: p.point_id,
      pointTitle: p.title,
      pointContent: p.text,
      pageReference: p.section,
    });
  }
  return {
    point_id: p.point_id,
    title: p.title,
    text: p.text,
    section: p.section,
  };
}

function rowFromGovPoint(
  p: GovPoint,
  isForAnalysis: (point: GovPoint) => boolean,
): LibraryPointDisplayRow {
  const sourced = p as SourcedGovPoint;
  const fallback = (sourced.pointNumber ?? p.point_id).trim().replace(/\.$/, '');
  const displayId =
    resolveGovPointDisplayNumber(sourced) ||
    (fallback && !/^[0-9a-f-]{36}$/i.test(fallback) && !isJunkExtractPointId(fallback)
      ? fallback
      : '');
  const forAnalysis = isForAnalysis(p);
  const classified = classifyGovPoint(p);
  const isIntroduction =
    Boolean(sourced.isIntroductionPoint) ||
    isSectionOnePoint(p.point_id, p.section ?? '') ||
    (!forAnalysis &&
      Boolean(
        classified.reason?.includes('§1') ||
          classified.reason?.includes('introduction') ||
          classified.reason?.includes('purpose') ||
          classified.reason?.includes('applicability'),
      ));
  return {
    point: sourced,
    displayId,
    forAnalysis,
    isIntroduction,
    depth: pointNumberDepth(displayId),
  };
}

function govPointsForGrouping(points: GovPoint[]): GovPoint[] {
  return points.map((p) => {
    const sourced = p as SourcedGovPoint;
    if (sourced.pointNumber) {
      // Keep regulationPointId / library metadata — only remap point_id for § grouping.
      return {
        ...sourced,
        point_id: sourced.pointNumber,
      };
    }
    return p;
  });
}

/** Stored row ids (point_id on SourcedGovPoint) that participate in gap analysis — same rule as regulation doc load. */
export function resolveComparablePointIdsForDoc(
  docPoints: SourcedGovPoint[],
  docName?: string | null,
): Set<string> {
  const name = docName ?? docPoints[0]?.docName;
  const grouping = docPoints.map((p) => ({
    point_id: p.pointNumber ?? p.point_id,
    title: p.title,
    text: p.text,
    section: p.section,
  }));
  const analyzed = analyzeGovPointSet(grouping, { docName: name });
  const comparableNums = new Set(analyzed.comparable.map((p) => p.point_id));
  return new Set(
    docPoints
      .filter((p) => comparableNums.has(p.pointNumber ?? p.point_id))
      .map((p) => p.point_id),
  );
}

/** Per regulation document: manual flat list or extracted § chapters. */
export function buildRegulationDocPointDisplay(
  docPoints: SourcedGovPoint[],
  isManual: boolean,
  comparablePointIds: Set<string>,
): Pick<LibraryPointDisplayDoc, 'chapters' | 'flatRows' | 'pointTree' | 'useChapters' | 'storedCount' | 'analyseCount'> {
  const isForAnalysis = (p: GovPoint) => comparablePointIds.has(p.point_id);

  if (isManual) {
    const flatRows = sortDisplayRows(docPoints.map((p) => rowFromGovPoint(p, isForAnalysis)));
    const pointTree = buildPointNumberTree(flatRows);
    return {
      chapters: [],
      flatRows,
      pointTree,
      useChapters: false,
      storedCount: docPoints.length,
      analyseCount: flatRows.filter((r) => r.forAnalysis).length,
    };
  }

  const display = buildPointDisplayChapters(govPointsForGrouping(docPoints), (groupGov) => {
    const num = groupGov.point_id.trim().replace(/\.$/, '');
    const src = docPoints.find(
      (p) => (p.pointNumber ?? p.point_id).trim().replace(/\.$/, '') === num,
    );
    return src ? isForAnalysis(src) : false;
  });

  return {
    chapters: display.chapters,
    flatRows: display.flatRows,
    pointTree: [],
    useChapters: display.useChapters,
    storedCount: docPoints.length,
    analyseCount: docPoints.filter((p) => comparablePointIds.has(p.point_id)).length,
  };
}

function chaptersFromGovGroups(
  grouped: GovPointChapterGroup[],
  sourcePoints: GovPoint[],
  isForAnalysis: (point: GovPoint) => boolean,
): LibraryPointDisplayChapter[] {
  const findPoint = (gov: GovPoint): GovPoint | undefined => {
    const govId = gov.point_id.trim().replace(/\.$/, '');
    return sourcePoints.find((p) => p.point_id.trim().replace(/\.$/, '') === govId);
  };

  return grouped.map((ch) => {
    const sections = ch.sections.map((sec) => {
      const rows: LibraryPointDisplayRow[] = [];
      for (const gov of sec.points) {
        const p = findPoint(gov);
        if (p) rows.push(rowFromGovPoint(p, isForAnalysis));
      }
      return {
        key: sec.key,
        label: formatSectionGroupLabel(sec.key, sectionHeadingTitleForKey(sec.key, sourcePoints)),
        rows: sortDisplayRows(rows),
      };
    });
    const chStored = sections.reduce((n, s) => n + s.rows.length, 0);
    const chAnalyse = sections.reduce(
      (n, s) => n + s.rows.filter((r) => r.forAnalysis).length,
      0,
    );
    return {
      chapter: ch.chapter,
      label: formatChapterLabel(ch.chapter, sectionHeadingTitleForKey(ch.chapter, sourcePoints)),
      sections,
      tree: chapterTreeFromRows(ch.chapter, sections.flatMap((s) => s.rows)),
      storedCount: chStored,
      analyseCount: chAnalyse,
    };
  });
}

function bucketPointsByTopChapter(
  points: GovPoint[],
  isForAnalysis: (point: GovPoint) => boolean,
): LibraryPointDisplayChapter[] {
  const byChapter = new Map<string, GovPoint[]>();
  for (const p of points) {
    const m = p.point_id.trim().match(/^(\d+)/);
    const ch = m?.[1] ?? 'other';
    const list = byChapter.get(ch) ?? [];
    list.push(p);
    byChapter.set(ch, list);
  }

  return [...byChapter.entries()]
    .sort(([a], [b]) => a.localeCompare(b, undefined, { numeric: true }))
    .map(([chapter, pts]) => {
      const rows = sortDisplayRows(pts.map((p) => rowFromGovPoint(p, isForAnalysis)));
      return {
        chapter,
        label: formatChapterLabel(chapter, sectionHeadingTitleForKey(chapter, points)),
        sections: [{ key: chapter, label: formatSectionGroupLabel(chapter, sectionHeadingTitleForKey(chapter, points)), rows }],
        tree: chapterTreeFromRows(chapter, rows),
        storedCount: pts.length,
        analyseCount: rows.filter((r) => r.forAnalysis).length,
      };
    });
}

/** §2 / §3 / §2.4 hierarchy for all stored points — headers + leaves in one tree. */
export function buildPointDisplayChapters(
  points: GovPoint[],
  isForAnalysis: (point: GovPoint) => boolean,
): { chapters: LibraryPointDisplayChapter[]; flatRows: LibraryPointDisplayRow[]; useChapters: boolean } {
  if (!points.length) {
    return { chapters: [], flatRows: [], useChapters: false };
  }

  const grouped = groupGovPointsForPicker(points);
  let chapters: LibraryPointDisplayChapter[];

  if (grouped.length === 1 && grouped[0].chapter === 'other') {
    const byChapter = groupGovPointsByChapter(enrichGovPointSectionsForPicker(points));
    if (byChapter.length > 0) {
      chapters = chaptersFromGovGroups(byChapter, points, isForAnalysis);
    } else {
      chapters = bucketPointsByTopChapter(points, isForAnalysis);
    }
  } else {
    chapters = chaptersFromGovGroups(grouped, points, isForAnalysis);
  }

  return {
    chapters,
    flatRows: [],
    useChapters: chapters.length > 0,
  };
}

/** All stored library points in §chapter → section → numbered rows (headers + leaves). */
export function buildLibraryStoredPointDisplay(
  rawPoints: SourcedGovPoint[],
  _analysePoints?: SourcedGovPoint[],
): LibraryPointDisplayTree[] {
  const byLib = new Map<string, SourcedGovPoint[]>();

  for (const p of rawPoints) {
    const { libraryId } = parseSourceKey(p);
    const list = byLib.get(libraryId) ?? [];
    list.push(p);
    byLib.set(libraryId, list);
  }

  const trees: LibraryPointDisplayTree[] = [];

  for (const [libraryId, libPoints] of byLib) {
    const libraryName = libPoints[0]?.libraryName ?? 'Regulation points library';
    const byDoc = new Map<string, SourcedGovPoint[]>();
    for (const p of libPoints) {
      const { docId } = parseSourceKey(p);
      const list = byDoc.get(docId) ?? [];
      list.push(p);
      byDoc.set(docId, list);
    }

    const documents: LibraryPointDisplayDoc[] = [];
    let libStored = 0;
    let libAnalyse = 0;

    for (const [docId, docPoints] of byDoc) {
      const docName = docPoints[0]?.docName ?? 'Regulation document';
      const docKey = `${libraryId}:${docId}`;
      const isManual = isManualRegulationSource(docName);
      const comparableIds = resolveComparablePointIdsForDoc(docPoints, docName);
      const display = buildRegulationDocPointDisplay(docPoints, isManual, comparableIds);

      documents.push({
        key: docKey,
        docId,
        docName,
        ...display,
      });
      libStored += display.storedCount;
      libAnalyse += display.analyseCount;
    }

    documents.sort((a, b) => a.docName.localeCompare(b.docName));
    trees.push({
      key: libraryId,
      libraryId,
      libraryName,
      documents,
      storedCount: libStored,
      analyseCount: libAnalyse,
    });
  }

  return trees.sort((a, b) => a.libraryName.localeCompare(b.libraryName));
}

function matchSourcedToGov(points: SourcedGovPoint[], gov: GovPoint): SourcedGovPoint | undefined {
  const govId = gov.point_id.trim().replace(/\.$/, '');
  return points.find((p) => p.point_id.trim().replace(/\.$/, '') === govId);
}

export function mapLibrarySnapshotToSourced(
  p: {
    regulationPointId: string;
    regulationDocumentId: string;
    pointSnapshot?: string | Record<string, unknown>;
  },
  meta: { libraryId: string; libraryName: string; docName: string },
): SourcedGovPoint {
  const snap =
    typeof p.pointSnapshot === 'string'
      ? (JSON.parse(p.pointSnapshot) as Record<string, unknown>)
      : (p.pointSnapshot ?? {});
  const pointNumber = String(snap['pointNumber'] ?? '').trim();
  const pageReference = String(snap['pageReference'] ?? '').trim();
  const docId = String(p.regulationDocumentId);
  return {
    point_id: pointNumber || String(p.regulationPointId),
    title: String(snap['pointTitle'] ?? ''),
    text: String(snap['pointContent'] ?? ''),
    section: pageReference || undefined,
    pointNumber: pointNumber || undefined,
    isIntroductionPoint: Boolean(snap['isIntroductionPoint'] ?? snap['is_introduction_point']),
    isAnnexPoint: Boolean(snap['isAnnexPoint'] ?? snap['is_annex_point']),
    sourceLabel: meta.libraryName,
    sourceKey: `${meta.libraryId}:${docId}`,
    regulationPointId: String(p.regulationPointId),
    libraryId: meta.libraryId,
    libraryName: meta.libraryName,
    docId,
    docName: meta.docName,
  };
}

/** Library → document → flat analyse points (checkbox list). */
export function buildLibraryPointHierarchy(
  rawPoints: SourcedGovPoint[],
  analysePoints: SourcedGovPoint[],
): LibraryHierarchyGroup[] {
  const libs = new Map<string, LibraryHierarchyGroup>();
  const docStored = new Map<string, number>();

  for (const p of rawPoints) {
    const { libraryId, docId } = parseSourceKey(p);
    const libKey = libraryId;
    const docKey = `${libraryId}:${docId}`;
    docStored.set(docKey, (docStored.get(docKey) ?? 0) + 1);

    if (!libs.has(libKey)) {
      libs.set(libKey, {
        key: libKey,
        libraryId,
        libraryName: p.libraryName ?? p.sourceLabel ?? 'Regulation points library',
        storedCount: 0,
        analyseCount: 0,
        documents: [],
      });
    }
  }

  for (const lib of libs.values()) {
    const docMap = new Map<string, LibraryDocGroup>();
    for (const p of rawPoints) {
      const { libraryId, docId } = parseSourceKey(p);
      if (libraryId !== lib.libraryId) continue;
      const docKey = `${libraryId}:${docId}`;
      if (!docMap.has(docKey)) {
        docMap.set(docKey, {
          key: docKey,
          docId,
          docName: p.docName ?? `Regulation document`,
          storedCount: docStored.get(docKey) ?? 0,
          points: [],
        });
      }
    }
    lib.documents = [...docMap.values()].sort((a, b) => a.docName.localeCompare(b.docName));
    lib.storedCount = lib.documents.reduce((n, d) => n + d.storedCount, 0);
  }

  for (const p of analysePoints) {
    const { libraryId, docId } = parseSourceKey(p);
    const lib = libs.get(libraryId);
    if (!lib) continue;
    const docKey = `${libraryId}:${docId}`;
    let doc = lib.documents.find((d) => d.key === docKey);
    if (!doc) {
      doc = {
        key: docKey,
        docId,
        docName: p.docName ?? 'Regulation document',
        storedCount: docStored.get(docKey) ?? 0,
        points: [],
      };
      lib.documents.push(doc);
      lib.documents.sort((a, b) => a.docName.localeCompare(b.docName));
    }
    doc.points.push(p);
    lib.analyseCount += 1;
  }

  return [...libs.values()].sort((a, b) => a.libraryName.localeCompare(b.libraryName));
}

export function isManualRegulationSource(docName?: string | null): boolean {
  const name = (docName ?? '').trim().toLowerCase();
  return name.includes('manual custom') || name === 'manual custom points';
}

/** Library curation: manual/custom points are always comparable; extracted regs use leaf filter. */
export function filterPointsForLibraryAnalysis(points: SourcedGovPoint[]): {
  comparable: SourcedGovPoint[];
  skipped: Array<{ point: GovPoint; reason: string }>;
} {
  const manual: SourcedGovPoint[] = [];
  const extracted: SourcedGovPoint[] = [];
  for (const p of points) {
    if (isManualRegulationSource(p.docName)) manual.push(p);
    else extracted.push(p);
  }

  const skipped: Array<{ point: GovPoint; reason: string }> = [];
  const comparable: SourcedGovPoint[] = [];

  for (const p of manual) {
    const text = (p.text ?? '').trim();
    const title = (p.title ?? '').trim();
    if (!text && !title) {
      skipped.push({ point: p, reason: 'empty manual point' });
    } else {
      comparable.push(p);
    }
  }

  const extractedResult = filterComparableGovLeafPoints(extracted);
  comparable.push(...(extractedResult.comparable as SourcedGovPoint[]));
  skipped.push(...extractedResult.skipped);

  return { comparable, skipped };
}

export function prepareLibraryPointsForAnalysis(rawPoints: SourcedGovPoint[]): {
  comparable: SourcedGovPoint[];
  unique: SourcedGovPoint[];
  duplicateGroups: GovPointDuplicateGroup[];
  skipped: Array<{ point: GovPoint; reason: string }>;
  storedCount: number;
} {
  const storedCount = rawPoints.length;
  const { comparable, skipped } = filterPointsForLibraryAnalysis(rawPoints);
  const { unique, duplicateGroups } = dedupeGovPointsByFingerprint(comparable);
  const withIds = assignUniqueLibraryPointIds(unique);
  return { comparable, unique: withIds, duplicateGroups, skipped, storedCount };
}

export function countComparableGovPoints(points: GovPoint[]): number {
  return filterComparableGovLeafPoints(points).comparable.length;
}

export type GovPointSetAnalysis = {
  storedCount: number;
  analyseCount: number;
  skippedCount: number;
  comparable: GovPoint[];
  skipped: Array<{ point: GovPoint; reason: string }>;
};

/** Raw stored rows vs leaf points used for gap analysis. */
export function analyzeGovPointSet(
  points: GovPoint[],
  options?: { docName?: string | null },
): GovPointSetAnalysis {
  if (options?.docName && isManualRegulationSource(options.docName)) {
    const sourced = points.map((p) => ({ ...p, docName: options.docName })) as SourcedGovPoint[];
    const { comparable, skipped } = filterPointsForLibraryAnalysis(sourced);
    return {
      storedCount: points.length,
      analyseCount: comparable.length,
      skippedCount: skipped.length,
      comparable,
      skipped,
    };
  }
  const { comparable, skipped } = filterComparableGovLeafPoints(points);
  return {
    storedCount: points.length,
    analyseCount: comparable.length,
    skippedCount: skipped.length,
    comparable,
    skipped,
  };
}

export function formatStoredAnalyseMeta(
  stored: number | null | undefined,
  analyse?: number | null,
): string {
  if (stored == null) return 'not extracted';
  if (stored === 0) return '0 stored';
  if (analyse != null && analyse !== stored) {
    return `${analyse} analyse · ${stored} stored`;
  }
  if (analyse != null) return `${analyse} pts`;
  return `${stored} stored`;
}

export function formatPointCountSummary(analysis: GovPointSetAnalysis): string {
  const stored = analysis.storedCount;
  const analyse = Math.min(analysis.analyseCount, stored);
  if (analyse === stored) {
    return `${stored} points`;
  }
  return `${stored} stored · ${analyse} compared in gap analysis`;
}
