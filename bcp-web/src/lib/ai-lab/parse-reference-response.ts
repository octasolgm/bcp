import { BATCH_MESSAGE_SEP, REFERENCE_FIELD_REGEX } from './constants';
import { getComplianceColorTier, TIER_UI, type ColorTier } from './color-tier';

export type ReferenceComplianceBlock = {
  title: string;
  body: string;
  referencePdf: string;
  /** Parsed Document Reference field (Regul forward / gap export). */
  documentReference: string;
  outputResponse: string;
  fulfilledClauses: string;
  status: string;
  confidence: string;
  correctiveAction: string;
  responsibility: string;
  gapAnalysis: string;
  fields: { label: string; value: string }[];
};

export type ParsedReferenceCitation = {
  page: string | null;
  section: string | null;
  quote: string | null;
};

function parseComplianceBlock(block: string): Omit<
  ReferenceComplianceBlock,
  'referencePdf' | 'documentReference' | 'outputResponse' | 'fulfilledClauses' | 'status' | 'confidence' | 'correctiveAction' | 'responsibility' | 'gapAnalysis'
> & {
  fields: { label: string; value: string }[];
} {
  const lines = block.split('\n');
  const fields: { label: string; value: string }[] = [];
  const headerLines: string[] = [];
  let currentField: { label: string; valueLines: string[] } | null = null;

  for (const line of lines) {
    const match = line.match(REFERENCE_FIELD_REGEX);
    if (match) {
      if (currentField) {
        fields.push({
          label: currentField.label,
          value: currentField.valueLines.join('\n').trim(),
        });
      }
      currentField = {
        label: match[1],
        valueLines: match[2] ? [match[2]] : [],
      };
    } else if (currentField) {
      currentField.valueLines.push(line);
    } else {
      headerLines.push(line);
    }
  }

  if (currentField) {
    fields.push({
      label: currentField.label,
      value: currentField.valueLines.join('\n').trim(),
    });
  }

  const nonEmptyHeader = headerLines.filter((l) => l.trim());
  return {
    title: nonEmptyHeader[0]?.trim() ?? '',
    body: nonEmptyHeader.slice(1).join('\n').trim(),
    fields,
  };
}

function fieldValue(
  fields: { label: string; value: string }[],
  label: string,
): string {
  return fields.find((f) => f.label === label)?.value?.trim() ?? '';
}

export function parseReferenceCitation(text: string): ParsedReferenceCitation {
  const trimmed = text.trim();
  if (!trimmed || /no corresponding procedure found/i.test(trimmed)) {
    return { page: null, section: null, quote: null };
  }

  const pageMatch = trimmed.match(/Page\s+(\d+(?:\s*[-–]\s*\d+)?)/i);
  // Prefer a tight section token (clause number or short name). Avoid swallowing
  // UUIDs / regulation titles that Landing sometimes pastes after "Section".
  const tightSection = trimmed.match(
    /Section\s+(\d+(?:\.\d+)*|[A-Za-z][\w./-]{0,40})(?=\s*[,:).]|\s*$)/i,
  );
  const looseSection = trimmed.match(/Section\s+([^:'"]+?)(?=\s*:\s*['"]|$)/i);
  let section = (tightSection?.[1] ?? looseSection?.[1] ?? '').trim() || null;
  if (section) {
    section = section.replace(/[).,:;\]]+\s*$/g, '').trim() || null;
    if (section && /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/i.test(section)) {
      const numbered = section.match(/\b(\d+(?:\.\d+)*)\b/);
      section = numbered?.[1] ?? null;
    } else if (section) {
      const numbered = section.match(/^(\d+(?:\.\d+)*)\b(.*)$/);
      if (numbered) {
        const rest = numbered[2].trim();
        if (!rest || rest.length > 24 || (/^[A-Z]/.test(rest) && rest.split(/\s+/).length >= 3)) {
          section = numbered[1];
        }
      } else if (section.length > 48) {
        section = section.slice(0, 45).trimEnd() + '…';
      }
    }
  }
  const quoteMatch = trimmed.match(/['"]([^'"]+)['"]/);

  return {
    page: pageMatch?.[1]?.trim() ?? null,
    section,
    quote: quoteMatch?.[1]?.trim() ?? null,
  };
}

/** Full policy extract body for display/export — not short citation quotes. */
export function resolvePolicyExtractText(block: ReferenceComplianceBlock | null): string {
  if (!block?.outputResponse?.trim()) return '';
  const text = block.outputResponse.trim();
  if (/no corresponding procedure found/i.test(text)) return '';
  if (/^see .+\.$/i.test(text) && text.length < 120) return '';
  return text;
}

function isGenericDocReference(ref: string): boolean {
  const s = ref.trim().toLowerCase();
  return !s || s === 'internal policy manual' || s === 'n/a' || s === '—';
}

/** Score how likely text is a document location reference (not verbatim policy body). */
export function scoreAsDocumentReference(text: string): number {
  const t = text.trim();
  if (!t) return 0;
  let score = 0;
  const pageCites =
    (t.match(/\(p\.?\s*\d+\)/gi) ?? []).length + (t.match(/\bp\.?\s*\d+/gi) ?? []).length;
  const sectionCites = (t.match(/\bsection\s+[\w./-]+/gi) ?? []).length;
  score += pageCites * 2 + sectionCites;
  if (t.length < 500) score += 1;
  if (/^see .+\.$/i.test(t)) score += 3;
  if (/—\s*["']/.test(t) && pageCites > 0) score += 2;
  if (pageCites >= 2 && t.length < 700) score += 2;
  return score;
}

/** Score how likely text is verbatim policy extract (narrative), not a ref list. */
export function scoreAsPolicyExtract(text: string): number {
  const t = text.trim();
  if (!t) return 0;
  if (/no corresponding procedure found/i.test(t)) return -5;
  if (/^see .+\.$/i.test(t) && t.length < 120) return -3;
  let score = 0;
  if (t.length > 120) score += 1;
  if (t.length > 250) score += 2;
  if (t.length > 500) score += 2;
  if (/how it works/i.test(t)) score += 3;
  const pageCites = (t.match(/\(p\.?\s*\d+\)/gi) ?? []).length;
  if (pageCites >= 3 && t.length < 450) score -= 3;
  if (scoreAsDocumentReference(t) >= 4 && t.length < 600) score -= 2;
  return score;
}

/** When parser or LLM swapped ref vs extract, put each column back in the right place. */
export function reconcileDocumentRefAndExtract(
  documentReference: string,
  policyExtract: string,
): { documentReference: string; policyExtract: string } {
  const ref = documentReference.trim();
  const extract = policyExtract.trim();
  if (!ref && !extract) return { documentReference: '', policyExtract: '' };

  const refAsRef = scoreAsDocumentReference(ref);
  const extractAsRef = scoreAsDocumentReference(extract);
  const refAsExtract = scoreAsPolicyExtract(ref);
  const extractAsExtract = scoreAsPolicyExtract(extract);

  const looksSwapped =
    refAsExtract > refAsRef + 1 &&
    extractAsRef > extractAsExtract + 1 &&
    ref.length > 80 &&
    extract.length > 0;

  if (looksSwapped) return { documentReference: extract, policyExtract: ref };
  return { documentReference: ref, policyExtract: extract };
}

/** Document reference for display/export — refs only, not policy body text. */
export function resolveDocumentReferenceText(block: ReferenceComplianceBlock | null): string {
  if (!block) return '';
  const docRef = block.documentReference?.trim() ?? '';
  const pdfRef = block.referencePdf?.trim() ?? '';
  const candidates = [docRef, pdfRef].filter((c) => c && !isGenericDocReference(c));
  if (!candidates.length) return '';

  let best = candidates[0];
  let bestScore = scoreAsDocumentReference(best);
  for (const c of candidates.slice(1)) {
    const score = scoreAsDocumentReference(c);
    if (score > bestScore) {
      best = c;
      bestScore = score;
    }
  }
  return best;
}

/** Best document reference + policy extract from landing / pass-2 blocks (Excel + UI). */
export function resolvePolicyRefAndExtract(
  landing: ReferenceComplianceBlock | null,
  llm: ReferenceComplianceBlock | null,
): { documentReference: string; policyExtract: string } {
  const refCandidates: string[] = [];
  const extractCandidates: string[] = [];

  for (const block of [landing, llm]) {
    if (!block) continue;
    const docRef = resolveDocumentReferenceText(block);
    if (docRef) refCandidates.push(docRef);
    const extract = resolvePolicyExtractText(block);
    if (extract) extractCandidates.push(extract);
    const rawOut = block.outputResponse?.trim();
    if (rawOut && !/no corresponding procedure found/i.test(rawOut)) {
      extractCandidates.push(rawOut);
    }
  }

  let bestRef = '';
  let bestRefScore = -1;
  for (const c of refCandidates) {
    const score = scoreAsDocumentReference(c);
    if (score > bestRefScore) {
      bestRefScore = score;
      bestRef = c;
    }
  }

  let bestExtract = '';
  let bestExtractScore = -1;
  for (const c of extractCandidates) {
    const score = scoreAsPolicyExtract(c);
    if (score > bestExtractScore) {
      bestExtractScore = score;
      bestExtract = c;
    }
  }

  return reconcileDocumentRefAndExtract(bestRef, bestExtract);
}

export function parseReferenceComplianceBlock(
  block: string,
): ReferenceComplianceBlock {
  const parsed = parseComplianceBlock(block);
  const status = fieldValue(parsed.fields, 'Comply Yes/No (Status)');
  const confidenceRaw = fieldValue(parsed.fields, 'Compliance Confidence %');

  return {
    title: parsed.title,
    body: parsed.body,
    referencePdf: fieldValue(parsed.fields, 'Reference PDF'),
    documentReference: fieldValue(parsed.fields, 'Document Reference'),
    outputResponse: fieldValue(parsed.fields, 'Output/Response'),
    fulfilledClauses: fieldValue(parsed.fields, 'Fulfilled clauses'),
    status,
    confidence: confidenceRaw,
    correctiveAction: fieldValue(parsed.fields, 'Corrective Action Plan'),
    responsibility: fieldValue(parsed.fields, 'Responsibility'),
    gapAnalysis: fieldValue(parsed.fields, 'Gap analysis'),
    fields: parsed.fields,
  };
}

export function looksLikeReferenceComplianceText(text: string): boolean {
  return text.split('\n').some((line) => REFERENCE_FIELD_REGEX.test(line));
}

function splitReferenceComplianceChunks(text: string): string[] {
  const trimmed = text.trim();
  if (!trimmed) return [];

  const byBatchSep = trimmed
    .split(BATCH_MESSAGE_SEP)
    .map((s) => s.trim())
    .filter(Boolean);
  if (byBatchSep.length > 1) return byBatchSep;

  const byLineSep = trimmed
    .split(/\n---\n/)
    .map((s) => s.trim())
    .filter(Boolean);
  if (byLineSep.length > 1) return byLineSep;

  const statusMarker = /^Comply Yes\/No \(Status\)\s*:/gim;
  const hits = [...trimmed.matchAll(statusMarker)];
  if (hits.length > 1) {
    const chunks: string[] = [];
    let start = 0;
    for (let i = 1; i < hits.length; i++) {
      const cutAt = hits[i].index ?? trimmed.length;
      const chunk = trimmed.slice(start, cutAt).trim();
      if (chunk) chunks.push(chunk);
      start = cutAt;
    }
    const tail = trimmed.slice(start).trim();
    if (tail) chunks.push(tail);
    if (chunks.length > 1) return chunks;
  }

  return [trimmed];
}

export function parseReferenceComplianceText(
  text: string,
): ReferenceComplianceBlock[] {
  const chunks = splitReferenceComplianceChunks(text);
  if (!chunks.length) return [];

  const blocks = chunks.map(parseReferenceComplianceBlock);

  return blocks.filter(
    (b) =>
      b.title ||
      b.body ||
      b.outputResponse ||
      b.fields.some((f) => f.value),
  );
}

export type CapGap = { index: number; missing: string; fix: string; priority?: string };

export function parseBulletLines(text: string): string[] {
  const raw = text.trim();
  if (!raw || /^none$/i.test(raw)) return [];

  const parts: string[] = [];
  for (const line of raw.split('\n')) {
    const trimmed = line.trim();
    if (!trimmed) continue;
    if (/•/.test(trimmed)) {
      parts.push(...trimmed.split(/\s*•\s*/).filter(Boolean));
    } else {
      parts.push(trimmed);
    }
  }

  return parts
    .map((l) =>
      l
        .replace(/^['"""`'']+/g, '')
        .replace(/['"""`'']+$/g, '')
        .replace(/^[•\-*✓]\s*/, '')
        .replace(/^\d+[.)]\s*/, '')
        .trim(),
    )
    .filter(Boolean);
}

export function hasDisplayableFulfilledClauses(text: string | undefined): boolean {
  if (!text?.trim()) return false;
  return parseBulletLines(text).length > 0;
}

/** Requirement body as readable lines (bullets when multiple obligations). */
export function requirementDisplayLines(body: string): string[] {
  const text = body
    .replace(/\*\*/g, '')
    .replace(/^#{1,6}\s*/gm, '')
    .replace(/\r\n/g, '\n')
    .trim();
  if (!text) return [];

  const subHeaders = text.match(/^\d+\.\d+\.\d+\s*[—–-]/gm);
  if ((subHeaders?.length ?? 0) >= 2) {
    return text.split(/\n\n+/).map((c) => c.trim()).filter(Boolean);
  }

  const numbered = text.split(/\(\d+\)\s+/).filter(Boolean);
  if (numbered.length >= 2) {
    return numbered.map((s, i) => {
      const t = s.trim().replace(/\.$/, '');
      return `${i + 1}. ${t}${t.endsWith('.') ? '' : '.'}`;
    });
  }

  const bulletLines = parseBulletLines(text);
  if (bulletLines.length >= 2) return bulletLines;

  if (text.length > 220) {
    const sentences = text
      .split(/\.\s+(?=[A-Z(])/)
      .map((s) => s.trim())
      .filter(Boolean);
    if (sentences.length >= 2) {
      return sentences.map((s) => (s.endsWith('.') ? s : `${s}.`));
    }
  }

  return [text];
}

import { normalizeGapPriority } from '../nd/gap-priority';

function splitFixAndPriority(fixRaw: string): { fix: string; priority: string } {
  const match = fixRaw.match(/\.\s*Priority:\s*(low|medium|higher|high|critical)\s*$/i);
  if (!match) return { fix: fixRaw.trim(), priority: '' };
  const fix = fixRaw.slice(0, match.index).trim().replace(/\.$/, '');
  return { fix, priority: normalizeGapPriority(match[1]) };
}

export function parseCapGaps(cap: string): CapGap[] {
  const raw = cap.trim();
  if (!raw || raw === 'N/A' || raw === '—') return [];

  let body = raw.replace(/^Gap\(s\):\s*/i, '').trim();
  const recommendedMatch = body.match(/\n\s*Recommended action:\s*([\s\S]*)$/i);
  const recommended = recommendedMatch?.[1]?.trim() ?? '';
  if (recommendedMatch) {
    body = body.slice(0, recommendedMatch.index).trim();
  }

  const chunks = body.split(/\(\d+\)\s*Missing:\s*/i).filter(Boolean);
  const gaps: CapGap[] = chunks.map((chunk, i) => {
    const fixSplit = chunk.split(/\.\s*Fix:\s*/i);
    if (fixSplit.length > 1) {
      const { fix, priority } = splitFixAndPriority(fixSplit.slice(1).join('. Fix: '));
      return {
        index: i + 1,
        missing: fixSplit[0].trim().replace(/\.$/, ''),
        fix,
        priority,
      };
    }
    const priorityOnly = chunk.match(/\.\s*Priority:\s*(low|medium|higher|high|critical)\s*$/i);
    const missing = priorityOnly
      ? chunk.slice(0, priorityOnly.index).trim().replace(/\.$/, '')
      : chunk.trim().replace(/\.$/, '');
    return {
      index: i + 1,
      missing,
      fix: recommended,
      priority: priorityOnly ? normalizeGapPriority(priorityOnly[1]) : '',
    };
  });

  if (gaps.length === 0 && body) {
    return [{ index: 1, missing: body, fix: recommended }];
  }
  if (recommended && gaps.length > 0 && !gaps[gaps.length - 1].fix) {
    gaps[gaps.length - 1].fix = recommended;
  }
  return gaps;
}

/** Serialize structured gaps back to the AI action-plan text format. */
export function serializeCapGaps(gaps: CapGap[]): string {
  if (!gaps.length) return '';
  const body = gaps
    .map((g) => {
      const priority = normalizeGapPriority(g.priority);
      let line = `(${g.index}) Missing: ${g.missing.trim()}.`;
      if (g.fix?.trim()) line += ` Fix: ${g.fix.trim()}`;
      if (priority) line += `. Priority: ${priority}`;
      return line;
    })
    .join('\n');
  return `Gap(s):\n${body}`;
}

export function referenceBlockBadgeLabel(block: ReferenceComplianceBlock): string {
  const tier = referenceBlockToTier(block);
  if (block.status === 'Non-Compliant') return 'NON-COMPLIANT';
  if (block.status === 'Partial Compliant') return 'PARTIAL COMPLIANT';
  if (block.status === 'Compliant') {
    const confMatch = block.confidence.match(/(\d+)/);
    const conf = confMatch ? Number(confMatch[1]) : null;
    return conf === 100 ? 'FULLY COMPLIANT' : `COMPLIANT · ${conf ?? '?'}%`;
  }
  return TIER_UI[tier].badgeLabel;
}

export function referenceBlockToTier(block: ReferenceComplianceBlock): ColorTier {
  const confMatch = block.confidence.match(/(\d+)/);
  const confidence = confMatch ? Number(confMatch[1]) : null;
  let status = block.status;
  if (!status) {
    const statusField = block.fields.find((f) =>
      f.label.includes('Status'),
    );
    status = statusField?.value ?? '';
  }
  return getComplianceColorTier({
    index: 0,
    title: block.title,
    body: block.body,
    fields: block.fields,
    status,
    confidence,
    needsAttention:
      status !== 'Compliant' ||
      (confidence !== null && confidence < 100),
  });
}

export function referenceBlockToPlainText(
  block: ReferenceComplianceBlock,
): string {
  const fieldValue = (label: string) =>
    block.fields.find((f) => f.label === label)?.value?.trim() ?? '';

  const push = (parts: string[], label: string, value: string | undefined) => {
    parts.push(`${label} :`);
    parts.push(value?.trim() || '—');
  };

  const parts: string[] = [];
  if (block.title) parts.push(block.title);
  if (block.body) parts.push(block.body);

  push(parts, 'Reference PDF', block.referencePdf || fieldValue('Reference PDF'));
  push(
    parts,
    'Document Reference',
    block.documentReference || fieldValue('Document Reference'),
  );
  push(
    parts,
    'Output/Response',
    block.outputResponse || fieldValue('Output/Response'),
  );
  push(
    parts,
    'Fulfilled clauses',
    block.fulfilledClauses || fieldValue('Fulfilled clauses'),
  );
  push(
    parts,
    'Comply Yes/No (Status)',
    block.status || fieldValue('Comply Yes/No (Status)'),
  );
  push(
    parts,
    'Compliance Confidence %',
    block.confidence || fieldValue('Compliance Confidence %'),
  );
  push(
    parts,
    'Corrective Action Plan',
    block.correctiveAction || fieldValue('Corrective Action Plan'),
  );
  push(
    parts,
    'Responsibility',
    block.responsibility || fieldValue('Responsibility'),
  );

  return parts.join('\n\n');
}

export { TIER_UI };
