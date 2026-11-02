import type { AnalysisPoint } from '../types';
import { parsePointSnapshot } from '../utils';

type RegulationDocResponse = { success: boolean; data?: unknown };

/**
 * Point id -> title of the regulation document that clause came from, so a run over several
 * regulations names the right document on every row. A document that can no longer be read
 * (for example a deleted one) is left out and the export falls back to the run's own name.
 */
export async function loadRegulationDocNamesByPoint(
  points: AnalysisPoint[],
  fetchDoc: (id: string) => Promise<RegulationDocResponse>,
): Promise<Map<string, string>> {
  const docIdByPoint = new Map<string, string>();
  for (const point of points) {
    const docId = parsePointSnapshot(point.pointSnapshot).regulationDocumentId?.trim();
    if (point.id && docId) docIdByPoint.set(point.id, docId);
  }

  const docIds = [...new Set(docIdByPoint.values())];
  const names = new Map<string, string>();
  await Promise.all(
    docIds.map(async (id) => {
      const res = await fetchDoc(id);
      const name = res.success ? regulationDocName(res.data) : '';
      if (name) names.set(id, name);
    }),
  );

  const byPoint = new Map<string, string>();
  for (const [pointId, docId] of docIdByPoint) {
    const name = names.get(docId);
    if (name) byPoint.set(pointId, name);
  }
  return byPoint;
}

/** The regulation endpoint names a document `name`; older rows use `title` or a file name instead. */
export function regulationDocName(doc: unknown): string {
  const d = (doc ?? {}) as { name?: string; title?: string; originalFileName?: string; fileName?: string };
  return d.name?.trim() || d.title?.trim() || d.originalFileName?.trim() || d.fileName?.trim() || '';
}

/** "provider / model" that judged a run; empty when the run recorded no model. */
export function regulLlmLabel(run?: { regulLlmProvider?: string | null; regulLlmModel?: string | null } | null): string {
  const model = run?.regulLlmModel?.trim();
  if (!model) return '';
  const provider = run?.regulLlmProvider?.trim();
  return provider ? `${provider} / ${model}` : model;
}
