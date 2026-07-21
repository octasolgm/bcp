import type { InternalDocument } from './types';
import {
  parseInternalDocIdsFromRunField,
  type PolicyDocCatalogEntry,
} from './policy-doc-resolve';

export function internalDocCatalogFromRunDetail(
  runDetail: unknown,
  knownDocs: InternalDocument[] = [],
): PolicyDocCatalogEntry[] {
  if (!runDetail || typeof runDetail !== 'object') return [];
  const run = (runDetail as { run?: Record<string, unknown> }).run;
  if (!run) return [];

  const ids = parseInternalDocIdsFromRunField(run['selectedInternalDocIds']);
  return ids.map((id) => {
    const known = knownDocs.find((d) => d.id === id);
    return {
      id,
      title: known?.title ?? null,
      originalFileName: known?.originalFileName ?? null,
    };
  });
}
