import {
  parseReferenceCitation,
  parseReferenceComplianceBlock,
  parseReferenceComplianceText,
} from '../ai-lab/parse-reference-response';

export type PolicyDocCatalogEntry = {
  id: string;
  title?: string | null;
  originalFileName?: string | null;
};

export type PolicyRefProof = {
  page: string;
  section: string | null;
  docId: string | null;
  docLabel: string;
  quote?: string | null;
};

function normalizeName(value: string): string {
  return value
    .toLowerCase()
    .replace(/\.(pdf|docx?)$/i, '')
    .replace(/[^a-z0-9]+/g, ' ')
    .trim();
}

/** Match Reference PDF / file name text to an internal document id. */
export function resolvePolicyDocId(
  reference: string | null | undefined,
  catalog: PolicyDocCatalogEntry[],
): string | null {
  const ref = reference?.trim();
  if (!ref || !catalog.length) return catalog.length === 1 ? catalog[0].id : null;

  const refNorm = normalizeName(ref);
  for (const doc of catalog) {
    const names = [doc.originalFileName, doc.title].filter(Boolean) as string[];
    for (const name of names) {
      const nameNorm = normalizeName(name);
      if (!nameNorm) continue;
      if (refNorm.includes(nameNorm) || nameNorm.includes(refNorm)) return doc.id;
    }
  }

  return catalog.length === 1 ? catalog[0].id : null;
}

export function docLabelForId(
  docId: string | null,
  catalog: PolicyDocCatalogEntry[],
): string {
  if (!docId) return 'Policy';
  const doc = catalog.find((d) => d.id === docId);
  if (!doc) return 'Policy';
  return (doc.originalFileName || doc.title || 'Policy').replace(/\.(pdf|docx?)$/i, '');
}

export function formatPolicyRefLabel(ref: PolicyRefProof, multiDoc = false): string {
  const parts: string[] = [];
  if (multiDoc && ref.docLabel) parts.push(ref.docLabel);
  if (ref.page) parts.push(`Page ${ref.page}`);
  if (ref.section) parts.push(`Section ${ref.section}`);
  return parts.join(', ') || 'Policy source';
}

/** Build per-page policy refs from AI messages, resolving doc id from Reference PDF when possible. */
export function buildPolicyRefProofs(
  landingMessage: string,
  llmMessage: string,
  catalog: PolicyDocCatalogEntry[],
): PolicyRefProof[] {
  const refs: PolicyRefProof[] = [];
  const seen = new Set<string>();

  for (const msg of [landingMessage, llmMessage]) {
    if (!msg?.trim()) continue;
    for (const block of parseReferenceComplianceText(msg)) {
      const source = block.outputResponse?.trim() ?? '';
      if (!source) continue;
      const cite = parseReferenceCitation(source);
      if (!cite.page && !cite.section) continue;
      const docId = resolvePolicyDocId(block.referencePdf, catalog);
      const pageKey = cite.page ?? cite.section ?? source.slice(0, 40);
      const key = `${docId ?? 'default'}:${pageKey}`;
      if (seen.has(key)) continue;
      seen.add(key);
      refs.push({
        page: cite.page ?? '',
        section: cite.section,
        docId,
        docLabel: docLabelForId(docId, catalog),
        quote: cite.quote,
      });
    }
  }

  return refs;
}

export function parseInternalDocIdsFromRunField(value: unknown): string[] {
  if (typeof value === 'string') {
    try {
      const parsed = JSON.parse(value) as unknown[];
      return parsed.filter((id): id is string => typeof id === 'string' && !!id.trim());
    } catch {
      return value.trim() ? [value.trim()] : [];
    }
  }
  if (Array.isArray(value)) {
    return value.filter((id): id is string => typeof id === 'string' && !!id.trim());
  }
  return [];
}
