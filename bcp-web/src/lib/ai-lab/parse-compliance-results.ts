import { COMPLIANCE_FIELD_REGEX } from './constants';

export type ComplianceField = { label: string; value: string };

export type ComplianceBlock = {
  title: string;
  body: string;
  fields: ComplianceField[];
};

export type ParsedComplianceResult = ComplianceBlock & {
  index: number;
  status: string;
  confidence: number | null;
  needsAttention: boolean;
};

export type ReportStats = {
  total: number;
  atFullConfidence: number;
  belowFullConfidence: number;
  compliant: number;
  partial: number;
  nonCompliant: number;
  attentionItems: ParsedComplianceResult[];
};

function parseComplianceBlock(block: string): ComplianceBlock {
  const lines = block.split('\n');
  const fields: ComplianceField[] = [];
  const headerLines: string[] = [];
  let currentField: { label: string; valueLines: string[] } | null = null;

  for (const line of lines) {
    const match = line.match(COMPLIANCE_FIELD_REGEX);
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

export function splitComplianceReport(text: string): ComplianceBlock[] {
  const trimmed = text.trim();
  if (!trimmed) return [];

  const chunks = trimmed
    .split(/\n---\n/)
    .map((s) => s.trim())
    .filter(Boolean);

  if (chunks.length > 1) {
    return chunks.map(parseComplianceBlock).filter(hasContent);
  }

  const single = parseComplianceBlock(trimmed);
  if (single.fields.length > 0) return [single];

  const multi = splitByRepeatedOutputResponse(trimmed);
  if (multi.length > 1) return multi;

  return hasContent(single) ? [single] : [];
}

function hasContent(block: ComplianceBlock): boolean {
  return Boolean(block.title || block.body || block.fields.length > 0);
}

/** Fallback when batch copy omitted --- separators */
function splitByRepeatedOutputResponse(text: string): ComplianceBlock[] {
  const parts = text.split(/(?=\nOutput\/Response\s*:)/i).filter((p) => p.trim());
  if (parts.length <= 1) return [];

  return parts.map((part, i) => {
    const block = parseComplianceBlock(part.trim());
    if (block.title) return block;
    if (i > 0 && parts[i - 1]) {
      const prevLines = parts[i - 1].trim().split('\n').filter((l) => l.trim());
      const titleLine = prevLines.find(
        (l) => !COMPLIANCE_FIELD_REGEX.test(l) && l.trim().length > 0,
      );
      if (titleLine) block.title = titleLine.trim();
    }
    return block;
  }).filter(hasContent);
}

export function parseConfidence(value: string): number | null {
  const m = value.match(/(\d+(?:\.\d+)?)/);
  if (!m) return null;
  return Math.min(100, Math.max(0, parseFloat(m[1])));
}

export function normalizeStatus(value: string): string {
  const v = value.trim();
  if (/non-?compliant/i.test(v)) return 'Non-Compliant';
  if (/partial/i.test(v)) return 'Partial Compliant';
  if (/compliant/i.test(v)) return 'Compliant';
  return v || 'Unknown';
}

function fieldValue(fields: ComplianceField[], labelPart: string): string {
  const f = fields.find((x) =>
    x.label.toLowerCase().includes(labelPart.toLowerCase()),
  );
  return f?.value ?? '';
}

export function enrichComplianceBlock(
  block: ComplianceBlock,
  index: number,
): ParsedComplianceResult {
  const status = normalizeStatus(fieldValue(block.fields, 'Status'));
  const confidence = parseConfidence(
    fieldValue(block.fields, 'Compliance Confidence'),
  );
  const needsAttention =
    (confidence !== null && confidence < 100) ||
    status === 'Partial Compliant' ||
    status === 'Non-Compliant';

  return {
    ...block,
    index,
    status,
    confidence,
    needsAttention,
  };
}

export function parseComplianceReport(text: string): ParsedComplianceResult[] {
  return splitComplianceReport(text).map(enrichComplianceBlock);
}

export function cleanDisplayTitle(title: string): string {
  return title.replace(/^#{1,3}\s*/, '').trim();
}

export function buildReportStats(
  results: ParsedComplianceResult[],
): ReportStats {
  const attentionItems = results.filter((r) => r.needsAttention);
  return {
    total: results.length,
    atFullConfidence: results.filter((r) => r.confidence === 100).length,
    belowFullConfidence: results.filter(
      (r) => r.confidence !== null && r.confidence < 100,
    ).length,
    compliant: results.filter((r) => r.status === 'Compliant').length,
    partial: results.filter((r) => r.status === 'Partial Compliant').length,
    nonCompliant: results.filter((r) => r.status === 'Non-Compliant').length,
    attentionItems,
  };
}

export const AI_SUMMARY_PROMPT_PREFIX = `You are a senior CBUAE/TFS compliance reporting analyst. Review the following batch gap-analysis results and produce a structured report with these sections:

## Executive Summary
3-5 sentences on overall compliance posture, key themes, and risk level.

## Attention Required (< 100% confidence or Partial/Non-Compliant)
Bullet list ONLY for points that need attention. For each: point title/ID, confidence %, status, and one-line risk note.

## Statistics
Table or bullets: total points, Compliant count, Partial count, Non-Compliant count, at 100% confidence vs below 100%.

## Recommended Priorities
Top 3 corrective actions ranked by regulatory risk.

Be concise and actionable. Use markdown.

---
BATCH RESULTS:

`;
