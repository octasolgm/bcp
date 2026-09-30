import type { InternalDocument } from './types';
import { compareDateIso, compareNumber, compareText, type SortDir } from './list-utils';

export type InternalDocCatalogGroupKind = 'analysis' | 'standalone';

export type InternalDocCatalogGroup = {
  kind: InternalDocCatalogGroupKind;
  key: string;
  runId: string | null;
  runName: string;
  docs: InternalDocument[];
};

export type InternalDocCatalogSortColumn = 'title' | 'uploaded' | 'size' | 'source' | 'pages' | 'analyses';

export function compareInternalDocumentsForCatalog(
  a: InternalDocument,
  b: InternalDocument,
  column: InternalDocCatalogSortColumn,
  sortDir: SortDir,
): number {
  switch (column) {
    case 'title':
      return compareText(a.title, b.title, sortDir);
    case 'size':
      return compareNumber(a.sizeBytes ?? 0, b.sizeBytes ?? 0, sortDir);
    case 'pages':
      return compareNumber(a.pageCount ?? 0, b.pageCount ?? 0, sortDir);
    case 'analyses':
      return compareNumber(a.analysisRunCount ?? 0, b.analysisRunCount ?? 0, sortDir);
    case 'source':
      return compareText(a.source ?? 'nd', b.source ?? 'nd', sortDir);
    case 'uploaded':
    default:
      return compareDateIso(a.uploaded, b.uploaded, sortDir);
  }
}

function segmentKeyFor(doc: InternalDocument): string {
  if (doc.generatedByAnalysis) {
    const runId = doc.generatedFromRunId?.trim();
    if (runId) return `run:${runId}`;
  }
  return 'standalone';
}

/** Insert group headers on a list already sorted by the catalog (e.g. uploaded date) — order is unchanged. */
export function groupInternalDocumentsByAnalysisSource(
  sortedDocs: InternalDocument[],
  _sortColumn: InternalDocCatalogSortColumn = 'uploaded',
  _sortDir: SortDir = 'desc',
): InternalDocCatalogGroup[] {
  if (sortedDocs.length === 0) return [];

  const groups: InternalDocCatalogGroup[] = [];
  let segmentKey = segmentKeyFor(sortedDocs[0]!);
  let segmentDocs: InternalDocument[] = [];

  const flush = () => {
    if (!segmentDocs.length) return;
    groups.push(buildGroup(segmentKey, segmentDocs));
    segmentDocs = [];
  };

  for (const doc of sortedDocs) {
    const key = segmentKeyFor(doc);
    if (segmentDocs.length > 0 && key !== segmentKey) {
      flush();
    }
    segmentKey = key;
    segmentDocs.push(doc);
  }
  flush();
  return groups;
}

function buildGroup(segmentKey: string, docs: InternalDocument[]): InternalDocCatalogGroup {
  if (segmentKey.startsWith('run:')) {
    const runId = segmentKey.slice('run:'.length);
    const runName = (docs[0]?.generatedFromRunName ?? '').trim() || 'Analysis run';
    return {
      kind: 'analysis',
      key: runId,
      runId,
      runName,
      docs: [...docs],
    };
  }
  return {
    kind: 'standalone',
    key: 'standalone',
    runId: null,
    runName: '',
    docs: [...docs],
  };
}

export function countInternalDocsForAnalysisRun(docs: InternalDocument[], runId: string | null): number {
  if (!runId?.trim()) return 0;
  const id = runId.trim();
  return docs.filter((d) => d.generatedByAnalysis && d.generatedFromRunId?.trim() === id).length;
}

export function catalogHasAnalysisGeneratedGroups(groups: InternalDocCatalogGroup[]): boolean {
  return groups.some((g) => g.kind === 'analysis' && g.docs.length > 0);
}

export function showInternalDocCatalogGroupHeader(
  group: InternalDocCatalogGroup,
  groups: InternalDocCatalogGroup[],
): boolean {
  if (!group.docs.length) return false;
  if (group.kind === 'analysis') return !!group.runId;
  return catalogHasAnalysisGeneratedGroups(groups);
}

export function hideInternalDocGeneratedFromSubline(
  doc: InternalDocument,
  groups: InternalDocCatalogGroup[],
): boolean {
  if (!doc.generatedByAnalysis || !catalogHasAnalysisGeneratedGroups(groups)) return false;
  return groups.some((g) => g.kind === 'analysis' && g.docs.some((d) => d.id === doc.id));
}
