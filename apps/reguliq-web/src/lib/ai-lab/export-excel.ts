import {
  parseBulletLines,
  parseCapGaps,
  requirementDisplayLines,
  type ReferenceComplianceBlock,
} from './parse-reference-response';
import {
  comparePointOrder,
  downloadExcelRows,
  normalizeMultiline,
} from './excel-write';

/** Client template — column A header (regulation / requirement source). */
export const DEFAULT_EXCEL_REQUIREMENT_HEADER =
  'CABINET DECISION NO. 74 (UN SC Resolutions / Combatting Terrorism, Terrorist / Proliferation Financing / WMD';

export const FORMATTED_COLUMN_HEADERS = [
  DEFAULT_EXCEL_REQUIREMENT_HEADER,
  'UAE Response / Compliance Level',
  'Comply Yes/No',
  'Action Plan',
  'Confidence %',
  'What Fulfills',
] as const;

const FORMATTED_COL_WIDTHS = [50, 60, 14, 45, 14, 45];

function complyYesNo(status: string): string {
  const s = status.trim();
  if (s === 'Compliant') return 'Yes';
  if (s === 'Partial Compliant') return 'Partial';
  if (s === 'Non-Compliant') return 'No';
  return s || '';
}

function requirementCell(block: ReferenceComplianceBlock): string {
  const title = block.title.trim();
  const reqLines = requirementDisplayLines(block.body);
  let body: string;
  if (reqLines.length > 1) {
    body = reqLines
      .map((line, i) => `${i + 1}. ${line.replace(/^\d+[.)]\s*/, '')}`)
      .join('\n');
  } else {
    body = block.body.trim();
  }
  if (title && body) return normalizeMultiline(`${title}\n\n${body}`);
  return normalizeMultiline(title || body || '');
}

function formatActionPlan(cap: string | undefined): string {
  const raw = cap?.trim();
  if (!raw || raw === 'N/A' || raw === '—') return '';

  const gaps = parseCapGaps(raw);
  if (gaps.length === 0) return normalizeMultiline(raw);

  return normalizeMultiline(
    gaps
      .map((g) => {
        const parts = [`Gap ${g.index} - Missing: ${g.missing}`];
        if (g.fix) parts.push(`Fix: ${g.fix}`);
        return parts.join('\n');
      })
      .join('\n\n'),
  );
}

function formatFulfilled(text: string | undefined): string {
  const lines = parseBulletLines(text ?? '');
  if (lines.length === 0) return '';
  return normalizeMultiline(lines.map((line, i) => `${i + 1}. ${line}`).join('\n'));
}

export async function downloadComplianceFormattedExcel(
  blocks: ReferenceComplianceBlock[],
  filename: string,
  requirementColumnHeader = DEFAULT_EXCEL_REQUIREMENT_HEADER,
): Promise<void> {
  if (blocks.length === 0) return;

  const headers: string[] = [...FORMATTED_COLUMN_HEADERS];
  headers[0] = requirementColumnHeader;

  const rows = [...blocks].sort(comparePointOrder).map((block) => [
    requirementCell(block),
    normalizeMultiline(block.outputResponse?.trim() || ''),
    complyYesNo(block.status),
    formatActionPlan(block.correctiveAction),
    block.confidence?.trim() || '',
    formatFulfilled(block.fulfilledClauses),
  ]);

  await downloadExcelRows(
    filename,
    'Compliance',
    headers,
    rows,
    FORMATTED_COL_WIDTHS,
  );
}

/** @deprecated use downloadComplianceFormattedExcel */
export const downloadComplianceExcel = downloadComplianceFormattedExcel;

/** @deprecated use FORMATTED_COLUMN_HEADERS */
export const EXCEL_COLUMN_HEADERS = FORMATTED_COLUMN_HEADERS;
