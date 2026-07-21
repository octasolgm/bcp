import { downloadComplianceDetailPdf } from '../ai-lab/export-pdf';
import { downloadComplianceFormattedExcel } from '../ai-lab/export-excel';
import {
  parseBulletLines,
  parseCapGaps,
  parseReferenceComplianceBlock,
  requirementDisplayLines,
  type ReferenceComplianceBlock,
} from '../ai-lab/parse-reference-response';
import {
  comparePointOrder,
  downloadExcelRows,
  normalizeMultiline,
} from '../ai-lab/excel-write';
import {
  landingBlocksFromReport,
  llmBlocksFromReport,
  exportableReportItems,
  type DualVerifyReportItem,
  type DualVerifyReportSummary,
} from '../dual-verify-report';

const DUAL_VERIFY_EXCEL_HEADERS = [
  'Point ID',
  'Agreement',
  'Landing Status',
  'LLM Status',
  'Landing Confidence',
  'LLM Confidence',
  'Agreement Summary',
  'Pass 1 — Landing AI',
  'Pass 2 — LLM Verify',
] as const;

function passExportFields(block: ReferenceComplianceBlock): string[] {
  return [
    block.status?.trim() || '',
    complyYesNo(block.status),
    block.confidence?.trim() || '',
    normalizeMultiline(block.outputResponse?.trim() || ''),
    formatActionPlan(block.correctiveAction),
    formatFulfilled(block.fulfilledClauses),
  ];
}

function complyYesNo(status: string): string {
  const s = status.trim();
  if (s === 'Compliant') return 'Yes';
  if (s === 'Partial Compliant') return 'Partial';
  if (s === 'Non-Compliant') return 'No';
  return s || '';
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

function requirementCellFromBlock(block: ReferenceComplianceBlock): string {
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

const BOTH_PASSES_EXCEL_HEADERS = [
  'Requirement',
  'Agreement',
  'Pass 1 — Status',
  'Pass 1 — Comply',
  'Pass 1 — Confidence %',
  'Pass 1 — UAE Response',
  'Pass 1 — Action Plan',
  'Pass 1 — What Fulfills',
  'Pass 2 — Status',
  'Pass 2 — Comply',
  'Pass 2 — Confidence %',
  'Pass 2 — UAE Response',
  'Pass 2 — Action Plan',
  'Pass 2 — What Fulfills',
] as const;

const BOTH_PASSES_COL_WIDTHS = [45, 18, 14, 10, 12, 50, 40, 40, 14, 10, 12, 50, 40, 40];

function requirementText(item: DualVerifyReportItem): string {
  const block = item.llmMessage
    ? parseReferenceComplianceBlock(item.llmMessage)
    : null;
  if (block) {
    const lines = requirementDisplayLines(block.body);
    const body =
      lines.length > 1
        ? lines.map((l, i) => `${i + 1}. ${l}`).join('\n')
        : block.body;
    return normalizeMultiline(
      [item.pointId, item.pointTitle, body].filter(Boolean).join('\n\n'),
    );
  }
  return normalizeMultiline(
    [item.pointId, item.pointTitle, item.govText].filter(Boolean).join('\n\n'),
  );
}

function passCell(message: string | undefined): string {
  if (!message?.trim()) return '';
  const block = parseReferenceComplianceBlock(message.trim());
  const parts = [
    block.status ? `Status: ${block.status}` : '',
    block.confidence ? `Confidence: ${block.confidence}` : '',
    block.outputResponse?.trim() || '',
    block.correctiveAction?.trim()
      ? `CAP: ${block.correctiveAction.trim()}`
      : '',
    block.fulfilledClauses?.trim()
      ? `Fulfilled:\n${block.fulfilledClauses.trim()}`
      : '',
  ].filter(Boolean);
  return normalizeMultiline(parts.join('\n\n'));
}

function sortedItems(items: DualVerifyReportItem[]): DualVerifyReportItem[] {
  return [...items].sort((a, b) =>
    comparePointOrder(
      { title: a.pointId } as ReturnType<typeof parseReferenceComplianceBlock>,
      { title: b.pointId } as ReturnType<typeof parseReferenceComplianceBlock>,
    ),
  );
}

function sortedExportable(items: DualVerifyReportItem[]): DualVerifyReportItem[] {
  return sortedItems(exportableReportItems(items));
}

export async function downloadDualVerifyExcel(
  items: DualVerifyReportItem[],
  filename: string,
): Promise<void> {
  const done = sortedExportable(items);
  if (!done.length) return;

  const rows = done.map((item) => {
    const landing = parseReferenceComplianceBlock(item.landingMessage!.trim());
    const llm = parseReferenceComplianceBlock(item.llmMessage!.trim());
    return [
      item.pointId,
      item.agreement?.label ?? '',
      item.agreement?.landingStatus ?? landing.status,
      item.agreement?.llmStatus ?? llm.status,
      item.agreement?.landingConfidence != null
        ? String(item.agreement.landingConfidence)
        : landing.confidence,
      item.agreement?.llmConfidence != null
        ? String(item.agreement.llmConfidence)
        : llm.confidence,
      normalizeMultiline(item.agreement?.summary ?? ''),
      passCell(item.landingMessage),
      passCell(item.llmMessage),
    ];
  });

  await downloadExcelRows(
    filename,
    'Dual Verify',
    [...DUAL_VERIFY_EXCEL_HEADERS],
    rows,
    [12, 22, 16, 16, 14, 14, 40, 55, 55],
  );
}

/** BCP formatted layout with Pass 1 (Landing AI) and Pass 2 (LLM) side by side */
export async function downloadDualVerifyBothPassesFormattedExcel(
  items: DualVerifyReportItem[],
  filename: string,
): Promise<void> {
  const done = sortedExportable(items);
  if (!done.length) return;

  const rows = done.map((item) => {
    const landing = parseReferenceComplianceBlock(item.landingMessage!.trim());
    const llm = parseReferenceComplianceBlock(item.llmMessage!.trim());
    const req =
      requirementText(item) ||
      requirementCellFromBlock(llm.title ? llm : landing);
    return [
      req,
      item.agreement?.label ?? '',
      ...passExportFields(landing),
      ...passExportFields(llm),
    ];
  });

  await downloadExcelRows(
    filename,
    'Dual Verify Both Passes',
    [...BOTH_PASSES_EXCEL_HEADERS],
    rows,
    BOTH_PASSES_COL_WIDTHS,
  );
}

/** Full PDF — both passes per point with status, confidence, output, fulfilled, CAP */
export async function downloadDualVerifyCombinedPdf(
  items: DualVerifyReportItem[],
  summary: DualVerifyReportSummary,
  filename: string,
): Promise<void> {
  const done = sortedExportable(items);
  if (!done.length) return;

  const { jsPDF } = await import('jspdf');
  const pdf = new jsPDF({ unit: 'mm', format: 'a4' });
  const margin = 12;
  const pageWidth = pdf.internal.pageSize.getWidth();
  const pageHeight = pdf.internal.pageSize.getHeight();
  const maxWidth = pageWidth - margin * 2;
  let y = margin;

  const ensureSpace = (need: number) => {
    if (y + need > pageHeight - margin) {
      pdf.addPage();
      y = margin;
    }
  };

  const addText = (
    text: string,
    size: number,
    opts?: { bold?: boolean; color?: [number, number, number] },
  ) => {
    pdf.setFont('helvetica', opts?.bold ? 'bold' : 'normal');
    pdf.setFontSize(size);
    if (opts?.color) pdf.setTextColor(...opts.color);
    else pdf.setTextColor(0, 0, 0);
    const lines = pdf.splitTextToSize(text, maxWidth);
    for (const line of lines) {
      ensureSpace(size * 0.5);
      pdf.text(line, margin, y);
      y += size * 0.42;
    }
    y += 1.5;
  };

  const addPassSection = (
    label: string,
    message: string,
    accent: [number, number, number],
  ) => {
    const block = parseReferenceComplianceBlock(message.trim());
    addText(label, 11, { bold: true, color: accent });
    addText(
      `Status: ${block.status || '—'}  ·  Confidence: ${block.confidence || '—'}`,
      9,
      { bold: true },
    );
    if (block.referencePdf?.trim()) {
      addText(`Reference: ${block.referencePdf.trim()}`, 8);
    }
    if (block.outputResponse?.trim()) {
      addText('Output / Response:', 9, { bold: true });
      addText(block.outputResponse.trim(), 8);
    }
    if (block.fulfilledClauses?.trim()) {
      addText('What fulfills:', 9, { bold: true });
      addText(formatFulfilled(block.fulfilledClauses), 8);
    }
    if (block.correctiveAction?.trim() && block.correctiveAction !== 'N/A') {
      addText('Corrective action plan:', 9, { bold: true });
      addText(formatActionPlan(block.correctiveAction), 8);
    }
    if (block.responsibility?.trim()) {
      addText(`Responsibility: ${block.responsibility.trim()}`, 8);
    }
    y += 2;
  };

  addText('Dual Verify — Combined Report (Pass 1 + Pass 2)', 16, { bold: true });
  addText(
    `${summary.completed} points · ${summary.aligned} aligned · ${summary.needsReview} need review · ${summary.failed} failed`,
    10,
  );
  y += 3;

  for (const item of done) {
    ensureSpace(20);
    pdf.setDrawColor(180, 180, 180);
    pdf.line(margin, y, pageWidth - margin, y);
    y += 4;

    addText(`${item.pointId}${item.pointTitle ? ` — ${item.pointTitle}` : ''}`, 12, {
      bold: true,
    });
    if (item.agreement) {
      addText(
        `Agreement: ${item.agreement.label} — ${item.agreement.summary}`,
        9,
      );
    }

    addPassSection('Pass 1 — Landing AI', item.landingMessage!, [13, 148, 136]);
    addPassSection('Pass 2 — LLM Verify', item.llmMessage!, [79, 70, 229]);
    y += 4;
  }

  pdf.save(filename);
}

export async function downloadDualVerifyDetailPdf(
  items: DualVerifyReportItem[],
  summary: DualVerifyReportSummary,
  filename: string,
  title = 'Dual Verify — Detailed Report (Pass 2)',
): Promise<void> {
  const blocks = sortedItems(items)
    .filter((i) => i.llmMessage)
    .map((i) => parseReferenceComplianceBlock(i.llmMessage!.trim()));

  if (!blocks.length) return;

  const summaryText = [
    `Dual verify report — ${summary.total} point(s) in combined report.`,
    `${summary.completed} completed · ${summary.aligned} aligned · ${summary.needsReview} need review · ${summary.failed} failed.`,
    summary.inProgress > 0
      ? `${summary.inProgress} still in progress (excluded from detail export).`
      : '',
  ]
    .filter(Boolean)
    .join('\n');

  await downloadComplianceDetailPdf(
    blocks,
    {
      total: summary.completed,
      atFullConfidence: blocks.filter((b) => /100/.test(b.confidence)).length,
      belowFullConfidence: blocks.filter(
        (b) => b.confidence && !/100/.test(b.confidence),
      ).length,
      compliant: blocks.filter((b) => b.status === 'Compliant').length,
      partial: blocks.filter((b) => /partial/i.test(b.status)).length,
      nonCompliant: blocks.filter((b) => /non/i.test(b.status)).length,
      attentionItems: [],
    },
    filename,
    title,
    summaryText,
  );
}

export async function downloadDualVerifySummaryPdf(
  items: DualVerifyReportItem[],
  summary: DualVerifyReportSummary,
  filename: string,
): Promise<void> {
  const done = sortedItems(items).filter(
    (i) => i.landingMessage && i.llmMessage && i.agreement,
  );
  if (!done.length) return;

  const { jsPDF } = await import('jspdf');
  const pdf = new jsPDF({ unit: 'mm', format: 'a4' });
  const margin = 14;
  const pageWidth = pdf.internal.pageSize.getWidth();
  const maxWidth = pageWidth - margin * 2;
  let y = margin;

  const addLine = (text: string, size = 10, bold = false) => {
    pdf.setFont('helvetica', bold ? 'bold' : 'normal');
    pdf.setFontSize(size);
    const lines = pdf.splitTextToSize(text, maxWidth);
    for (const line of lines) {
      if (y > pdf.internal.pageSize.getHeight() - margin) {
        pdf.addPage();
        y = margin;
      }
      pdf.text(line, margin, y);
      y += size * 0.45;
    }
    y += 2;
  };

  addLine('Dual Verify — Summary Report', 16, true);
  addLine(
    `${summary.total} points · ${summary.completed} completed · ${summary.aligned} aligned · ${summary.needsReview} need review · ${summary.failed} failed`,
    10,
  );
  y += 4;

  for (const item of done) {
    addLine(`${item.pointId} — ${item.agreement!.label}`, 11, true);
    addLine(
      `Landing: ${item.agreement!.landingStatus} · LLM: ${item.agreement!.llmStatus}`,
      9,
    );
    addLine(item.agreement!.summary, 9);
    addLine(requirementText(item), 8);
    y += 3;
  }

  pdf.save(filename);
}

export async function downloadDualVerifyFormattedExcel(
  items: DualVerifyReportItem[],
  filename: string,
): Promise<void> {
  const blocks = llmBlocksFromReport(items);
  if (!blocks.length) return;
  await downloadComplianceFormattedExcel(blocks, filename);
}

export async function downloadDualVerifyPass1DetailPdf(
  items: DualVerifyReportItem[],
  summary: DualVerifyReportSummary,
  filename: string,
): Promise<void> {
  const blocks = landingBlocksFromReport(items);
  if (!blocks.length) return;
  const summaryText = [
    `Dual verify Pass 1 (Landing AI) — ${summary.completed} point(s).`,
    `${summary.aligned} aligned · ${summary.needsReview} need review.`,
  ].join(' ');
  await downloadComplianceDetailPdf(
    blocks,
    {
      total: summary.completed,
      atFullConfidence: blocks.filter((b) => /100/.test(b.confidence)).length,
      belowFullConfidence: blocks.filter(
        (b) => b.confidence && !/100/.test(b.confidence),
      ).length,
      compliant: blocks.filter((b) => b.status === 'Compliant').length,
      partial: blocks.filter((b) => /partial/i.test(b.status)).length,
      nonCompliant: blocks.filter((b) => /non/i.test(b.status)).length,
      attentionItems: [],
    },
    filename,
    'Dual Verify — Pass 1 (Landing AI)',
    summaryText,
  );
}
