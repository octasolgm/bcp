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
  dir: SortDir,
): number {
  switch (column) {
    case 'title':
      return compareText(a.title, b.title, dir);
    case 'size':
      return compareNumber(a.sizeBytes ?? 0, b.sizeBytes ?? 0, dir);
    case 'pages':
      return compareNumber(a.pageCount ?? 0, b.pageCount ?? 0, dir);
    case 'analyses':
      return compareNumber(a.analysisRunCount ?? 0, b.analysisRunCount ?? 0, dir);
    case 'source':
      return compareText(a.source ?? 'nd', b.source ?? 'nd', dir);
    case 'uploaded':
    default:
      return compareDateIso(a.uploaded, b.uploaded, dir);
  }
}

/** Cluster filtered/sorted docs by originating analysis run; direct uploads stay in one block at the top. */
export function groupInternalDocumentsByAnalysisSource(
  docs: InternalDocument[],
  sortColumn: InternalDocCatalogSortColumn,
  sortDir: SortDir,
): InternalDocCatalogGroup[] {
  const standalone: InternalDocument[] = [];
  const byKey = new Map<string, { runId: string | null; runName: string; docs: InternalDocument[] }>();

  for (const d of docs) {
    if (d.generatedByAnalysis && (d.generatedFromRunId || d.generatedFromRunName)) {
      const runId = d.generatedFromRunId?.trim() || null;
      const runName = (d.generatedFromRunName ?? '').trim() || 'Analysis run';
      const key = runId ?? `name:${runName.toLowerCase()}`;
      let bucket = byKey.get(key);
      if (!bucket) {
        bucket = { runId, runName, docs: [] };
        byKey.set(key, bucket);
      }
      bucket.docs.push(d);
    } else {
      standalone.push(d);
    }
  }

  const cmp = (a: InternalDocument, b: InternalDocument) =>
    compareInternalDocumentsForCatalog(a, b, sortColumn, sortDir);

  const analysisGroups: InternalDocCatalogGroup[] = [...byKey.values()].map((b) => ({
    kind: 'analysis',
    key: b.runId ?? `name:${b.runName.toLowerCase()}`,
    runId: b.runId,
    runName: b.runName,
    docs: [...b.docs].sort(cmp),
  }));

  analysisGroups.sort((ga, gb) => cmp(ga.docs[0]!, gb.docs[0]!));

  const result: InternalDocCatalogGroup[] = [];
  if (standalone.length) {
    result.push({
      kind: 'standalone',
      key: 'standalone',
      runId: null,
      runName: '',
      docs: [...standalone].sort(cmp),
    });
  }
  result.push(...analysisGroups);
  return result;
}

export function catalogHasAnalysisGeneratedGroups(groups: InternalDocCatalogGroup[]): boolean {
  return groups.some((g) => g.kind === 'analysis' && g.docs.length > 0);
}

export function showInternalDocCatalogGroupHeader(
  group: InternalDocCatalogGroup,
  groups: InternalDocCatalogGroup[],
): boolean {
  if (!group.docs.length) return false;
  if (group.kind === 'analysis') return true;
  return catalogHasAnalysisGeneratedGroups(groups);
}

export function hideInternalDocGeneratedFromSubline(
  doc: InternalDocument,
  groups: InternalDocCatalogGroup[],
): boolean {
  if (!doc.generatedByAnalysis || !catalogHasAnalysisGeneratedGroups(groups)) return false;
  return groups.some((g) => g.kind === 'analysis' && g.docs.some((d) => d.id === doc.id));
}
