import type { InternalDocument } from './types';

/** Catalog label for stored-document versioning (upload v1, finalize v2+, etc.). */
export function internalDocVersionLabel(doc: InternalDocument): string | null {
  const v = doc.version ?? 0;
  if (doc.generatedByAnalysis) return `v${Math.max(1, v)}`;
  if (v > 1) return `v${v}`;
  return null;
}
