import { BATCH_MESSAGE_SEP, REFERENCE_FIELD_REGEX } from './constants';
import { getComplianceColorTier, TIER_UI, type ColorTier } from './color-tier';

export type ReferenceComplianceBlock = {
  title: string;
  body: string;
  referencePdf: string;
  outputResponse: string;
  fulfilledClauses: string;
  status: string;
  confidence: string;
  correctiveAction: string;
  responsibility: string;
  fields: { label: string; value: string }[];
};

export type ParsedReferenceCitation = {
  page: string | null;
  section: string | null;
  quote: string | null;
};

function parseComplianceBlock(block: string): Omit<
  ReferenceComplianceBlock,
  'referencePdf' | 'outputResponse' | 'fulfilledClauses' | 'status' | 'confidence' | 'correctiveAction' | 'responsibility'
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
  const sectionMatch = trimmed.match(
    /Section\s+([^:'"]+?)(?=\s*:\s*['"]|$)/i,
  );
  const quoteMatch = trimmed.match(/['"]([^'"]+)['"]/);

  return {
    page: pageMatch?.[1]?.trim() ?? null,
    section: sectionMatch?.[1]?.trim() ?? null,
    quote: quoteMatch?.[1]?.trim() ?? null,
  };
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
    outputResponse: fieldValue(parsed.fields, 'Output/Response'),
    fulfilledClauses: fieldValue(parsed.fields, 'Fulfilled clauses'),
    status,
    confidence: confidenceRaw,
    correctiveAction: fieldValue(parsed.fields, 'Corrective Action Plan'),
    responsibility: fieldValue(parsed.fields, 'Responsibility'),
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

export type CapGap = { index: number; missing: string; fix: string };

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
      return {
        index: i + 1,
        missing: fixSplit[0].trim().replace(/\.$/, ''),
        fix: fixSplit.slice(1).join('. Fix: ').trim(),
      };
    }
    return { index: i + 1, missing: chunk.trim().replace(/\.$/, ''), fix: recommended };
  });

  if (gaps.length === 0 && body) {
    return [{ index: 1, missing: body, fix: recommended }];
  }
  if (recommended && gaps.length > 0 && !gaps[gaps.length - 1].fix) {
    gaps[gaps.length - 1].fix = recommended;
  }
  return gaps;
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
