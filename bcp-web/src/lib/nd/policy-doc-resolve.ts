import {
  parseReferenceCitation,
  parseReferenceComplianceText,
  parseBulletLines,
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

const UUID_RE =
  /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/i;

/**
 * Clean AI "Section …" labels. Landing often pastes regulation point UUIDs/titles
 * or trailing punctuation into the citation (e.g. "Section 2)." or
 * "Section &lt;uuid&gt; International Legislative…").
 */
export function sanitizePolicySection(section: string | null | undefined): string | null {
  if (!section?.trim()) return null;
  let s = section.trim();

  // Drop trailing junk from malformed cites: "2).", "7.2):", "Legal Basis)."
  s = s.replace(/[).,:;\]]+\s*$/g, '').trim();
  if (!s) return null;

  if (UUID_RE.test(s)) {
    const withoutUuid = s.replace(UUID_RE, ' ').replace(/\s+/g, ' ').trim();
    const numbered = withoutUuid.match(/^(\d+(?:\.\d+)*)\b/) ?? s.match(/\b(\d+(?:\.\d+)*)\b/);
    // UUID + regulation title leak — keep only a short clause number if present.
    if (numbered && numbered[1].length <= 16) return numbered[1];
    return null;
  }

  // Prefer clause numbers when the rest looks like a long document title.
  const numbered = s.match(/^(\d+(?:\.\d+)*)\b(.*)$/);
  if (numbered) {
    const num = numbered[1];
    const rest = numbered[2].trim();
    if (!rest) return num;
    if (rest.length > 24) return num;
    // "2 International…" style — number only
    if (/^[A-Z]/.test(rest) && rest.split(/\s+/).length >= 3) return num;
    return num;
  }

  // Named sections ("Legal Basis") — keep short; drop long prose.
  if (s.length > 48) return s.slice(0, 45).trimEnd() + '…';
  return s;
}

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

  // Multi-doc Reference PDF lines like "manual-a.pdf + manual-b.pdf"
  if (ref.includes('+')) {
    for (const part of ref.split('+')) {
      const id = resolvePolicyDocId(part.trim(), catalog);
      if (id) return id;
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
  const section = sanitizePolicySection(ref.section);
  if (section) parts.push(`Section ${section}`);
  return parts.join(', ') || 'Policy source';
}

/** Parse one citation line, optionally prefixed with [Document Name], */
export function parsePolicyCitationFromLine(line: string): {
  docName: string | null;
  page: string | null;
  section: string | null;
  quote: string | null;
} {
  let trimmed = line.trim();
  if (!trimmed || /^none$/i.test(trimmed)) {
    return { docName: null, page: null, section: null, quote: null };
  }

  trimmed = trimmed.replace(/^•\s*/, '').replace(/^[-*]\s*/, '');

  let docName: string | null = null;
  const bracketDoc = trimmed.match(/^\[([^\]]+)\],\s*/);
  if (bracketDoc) {
    docName = bracketDoc[1].trim();
    trimmed = trimmed.slice(bracketDoc[0].length);
  } else {
    const dashDoc = trimmed.match(/^(.+?)\s+—\s+/);
    if (dashDoc && /section|page/i.test(trimmed)) {
      const candidate = dashDoc[1].trim();
      if (candidate.length <= 120 && !/must|shall|should|required/i.test(candidate)) {
        docName = candidate;
        trimmed = trimmed.slice(dashDoc[0].length);
      }
    }
  }

  // Prefer "Section 7.28, Page 10: quote" — capture a tight section token first.
  const structured = trimmed.match(
    /Section\s+(\d+(?:\.\d+)*|[A-Za-z][\w./-]{0,40})(?:,\s*Page\s+(\d+(?:\s*[-–]\s*\d+)?))?\s*:\s*['"]([^'"]+)['"]/i,
  );
  if (structured) {
    return {
      docName,
      section: sanitizePolicySection(structured[1]),
      page: structured[2]?.trim() ?? null,
      quote: structured[3]?.trim() ?? null,
    };
  }

  const pageFirst = trimmed.match(
    /Page\s+(\d+(?:\s*[-–]\s*\d+)?)(?:,\s*Section\s+([^:'"]+?))?\s*:\s*['"]([^'"]+)['"]/i,
  );
  if (pageFirst) {
    return {
      docName,
      page: pageFirst[1]?.trim() ?? null,
      section: sanitizePolicySection(pageFirst[2]),
      quote: pageFirst[3]?.trim() ?? null,
    };
  }

  const cite = parseReferenceCitation(trimmed);
  return {
    docName,
    page: cite.page,
    section: sanitizePolicySection(cite.section),
    quote: cite.quote,
  };
}

function splitCitationSourceLines(text: string): string[] {
  const raw = text.trim();
  if (!raw || /^none$/i.test(raw)) return [];

  const lines = raw.split(/\n+/).flatMap((line) => {
    const t = line.trim();
    if (!t) return [];
    if (/^•/.test(t) || t.includes(' — ')) return parseBulletLines(t);
    return [t];
  });

  return lines
    .map((l) => l.trim())
    .filter(Boolean)
    .filter((l) => !/^none$/i.test(l));
}

function resolveDocForCitation(
  docName: string | null,
  blockReferencePdf: string,
  catalog: PolicyDocCatalogEntry[],
): string | null {
  if (docName) {
    const fromName = resolvePolicyDocId(docName, catalog);
    if (fromName) return fromName;
  }
  return resolvePolicyDocId(blockReferencePdf, catalog);
}

function pushPolicyRef(
  refs: PolicyRefProof[],
  seen: Set<string>,
  catalog: PolicyDocCatalogEntry[],
  line: string,
  blockReferencePdf: string,
): void {
  const parsed = parsePolicyCitationFromLine(line);
  const section = sanitizePolicySection(parsed.section);
  if (!parsed.page && !section) return;
  // Skip empty or UUID-junk cites with no usable quote (noise from bad AI lines).
  if (!parsed.quote?.trim() && section == null && !parsed.page) return;

  const docId = resolveDocForCitation(parsed.docName, blockReferencePdf, catalog);
  const quoteKey = (parsed.quote ?? '').slice(0, 80).toLowerCase();
  const pageKey = parsed.page ?? section ?? line.slice(0, 40);
  const key = `${docId ?? parsed.docName ?? 'default'}:${pageKey}:${section ?? ''}:${quoteKey}`;
  if (seen.has(key)) return;
  // Same quote already shown under another junk section — keep first clean one.
  if (quoteKey.length >= 20) {
    for (const existing of refs) {
      const eq = (existing.quote ?? '').slice(0, 80).toLowerCase();
      if (eq === quoteKey) return;
    }
  }
  seen.add(key);

  refs.push({
    page: parsed.page ?? '',
    section,
    docId,
    docLabel: docLabelForId(docId, catalog),
    quote: parsed.quote,
  });
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
      const refPdf = block.referencePdf?.trim() ?? '';
      const sources = [block.outputResponse, block.fulfilledClauses].filter(Boolean) as string[];
      const beforeCount = refs.length;

      for (const source of sources) {
        for (const line of splitCitationSourceLines(source)) {
          pushPolicyRef(refs, seen, catalog, line, refPdf);
        }
      }

      // Fallback: single citation parse for legacy v1 one-line output
      if (refs.length === beforeCount && block.outputResponse?.trim()) {
        const cite = parseReferenceCitation(block.outputResponse);
        const section = sanitizePolicySection(cite.section);
        if (cite.page || section) {
          const docId = resolvePolicyDocId(refPdf, catalog);
          const pageKey = cite.page ?? section ?? block.outputResponse.slice(0, 40);
          const key = `${docId ?? 'default'}:${pageKey}:${section ?? ''}`;
          if (!seen.has(key)) {
            seen.add(key);
            refs.push({
              page: cite.page ?? '',
              section,
              docId,
              docLabel: docLabelForId(docId, catalog),
              quote: cite.quote,
            });
          }
        }
      }
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
