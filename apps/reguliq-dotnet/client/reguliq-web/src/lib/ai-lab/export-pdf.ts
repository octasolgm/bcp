import {
  buildTierCounts,
  COLOR_LEGEND,
  getComplianceColorTier,
  type ColorTier,
} from './color-tier';
import type { FullReportExport } from './export-report';
import type { ParsedComplianceResult, ReportStats } from './parse-compliance-results';
import {
  hasDisplayableFulfilledClauses,
  parseBulletLines,
  parseCapGaps,
  parseReferenceCitation,
  referenceBlockBadgeLabel,
  referenceBlockToTier,
  requirementDisplayLines,
  type ReferenceComplianceBlock,
} from './parse-reference-response';

const TIER_PDF: Record<
  ColorTier,
  {
    fill: [number, number, number];
    border: [number, number, number];
    text: [number, number, number];
    label: string;
    badge: string;
  }
> = {
  green: {
    fill: [236, 253, 245],
    border: [52, 211, 153],
    text: [6, 95, 70],
    label: 'Fully compliant',
    badge: 'FULLY COMPLIANT',
  },
  yellow: {
    fill: [255, 251, 235],
    border: [251, 191, 36],
    text: [146, 64, 14],
    label: 'Review needed',
    badge: 'REVIEW NEEDED',
  },
  red: {
    fill: [254, 242, 242],
    border: [248, 113, 113],
    text: [185, 28, 28],
    label: 'Action required',
    badge: 'NON-COMPLIANT',
  },
  neutral: {
    fill: [248, 250, 252],
    border: [203, 213, 225],
    text: [71, 85, 105],
    label: 'Unrated',
    badge: 'UNRATED',
  },
};

const FULFILLED_PDF: Record<
  ColorTier,
  { fill: [number, number, number]; border: [number, number, number]; text: [number, number, number] }
> = {
  green: { fill: [236, 253, 245], border: [167, 243, 208], text: [6, 78, 59] },
  yellow: { fill: [255, 251, 235], border: [253, 230, 138], text: [146, 64, 14] },
  red: { fill: [254, 242, 242], border: [254, 202, 202], text: [185, 28, 28] },
  neutral: { fill: [248, 250, 252], border: [203, 213, 225], text: [71, 85, 105] },
};

function stripMarkdown(text: string): string {
  return text
    .replace(/\*\*/g, '')
    .replace(/^#{1,6}\s*/gm, '')
    .replace(/^- /gm, '• ')
    .replace(/•\s*#{1,6}\s*/g, '• ')
    .replace(/\r\n/g, '\n')
    .trim();
}

function cleanTitle(title: string): string {
  return stripMarkdown(title.replace(/^#{1,6}\s*/, '')).trim();
}

/** Remove attention blocks from summary — PDF renders attention in its own section. */
function summaryForPdf(summary: string): string {
  const text = stripMarkdown(summary);
  const cut = text.search(
    /\n\s*(attention\s+(required|focus)|formatted results)/i,
  );
  if (cut > 0) return text.slice(0, cut).trim();
  return text.trim();
}

class PdfWriter {
  private y: number;
  private readonly margin = 14;
  private readonly pageWidth: number;
  private readonly pageHeight: number;
  private readonly maxWidth: number;

  constructor(private readonly pdf: import('jspdf').jsPDF) {
    this.pageWidth = pdf.internal.pageSize.getWidth();
    this.pageHeight = pdf.internal.pageSize.getHeight();
    this.maxWidth = this.pageWidth - this.margin * 2;
    this.y = this.margin;
  }

  private lineHeight(fontSize: number): number {
    return fontSize * 0.42;
  }

  private newPage(): void {
    this.pdf.addPage();
    this.y = this.margin;
  }

  ensureSpace(mm: number): void {
    if (this.y + mm > this.pageHeight - this.margin) {
      this.newPage();
    }
  }

  gap(mm = 4): void {
    this.y += mm;
  }

  writeln(
    text: string,
    fontSize = 10,
    opts?: {
      bold?: boolean;
      italic?: boolean;
      color?: [number, number, number];
      indent?: number;
      maxWidth?: number;
    },
  ): void {
    if (!text.trim()) return;
    const indent = opts?.indent ?? 0;
    const width = (opts?.maxWidth ?? this.maxWidth) - indent;
    const x = this.margin + indent;
    this.pdf.setFontSize(fontSize);
    const style = opts?.italic ? 'italic' : opts?.bold ? 'bold' : 'normal';
    this.pdf.setFont('helvetica', style);
    const color = opts?.color ?? [30, 41, 59];
    this.pdf.setTextColor(color[0], color[1], color[2]);
    const lines = this.pdf.splitTextToSize(text, width) as string[];
    for (const line of lines) {
      this.ensureSpace(this.lineHeight(fontSize));
      this.pdf.text(line, x, this.y);
      this.y += this.lineHeight(fontSize);
    }
  }

  drawSectionLabel(label: string, cx: number, innerW: number): void {
    this.gap(2);
    this.pdf.setFontSize(7.5);
    this.pdf.setFont('helvetica', 'bold');
    this.pdf.setTextColor(71, 85, 105);
    this.pdf.text(label.toUpperCase(), cx, this.y);
    this.y += 4;
  }

  sectionTitle(title: string): void {
    this.gap(6);
    this.writeln(title, 13, { bold: true, color: [76, 29, 149] });
    this.gap(2);
  }

  drawStatusBoxes(compliant: number, partial: number, nonCompliant: number): void {
    const gap = 3;
    const boxW = (this.maxWidth - gap * 2) / 3;
    const boxH = 24;
    this.ensureSpace(boxH + 5);
    const startY = this.y;

    const items: {
      label: string;
      value: number;
      colors: (typeof TIER_PDF)['green'];
    }[] = [
      { label: 'Compliant', value: compliant, colors: TIER_PDF.green },
      { label: 'Partial', value: partial, colors: TIER_PDF.yellow },
      { label: 'Non-Compliant', value: nonCompliant, colors: TIER_PDF.red },
    ];

    items.forEach((item, i) => {
      const x = this.margin + i * (boxW + gap);
      this.pdf.setFillColor(...item.colors.fill);
      this.pdf.setDrawColor(...item.colors.border);
      this.pdf.setLineWidth(0.55);
      this.pdf.roundedRect(x, startY, boxW, boxH, 2, 2, 'FD');
      this.pdf.setFontSize(8);
      this.pdf.setFont('helvetica', 'bold');
      this.pdf.setTextColor(...item.colors.text);
      this.pdf.text(item.label.toUpperCase(), x + boxW / 2, startY + 9, {
        align: 'center',
      });
      this.pdf.setFontSize(20);
      this.pdf.text(String(item.value), x + boxW / 2, startY + 19, {
        align: 'center',
      });
    });

    this.y = startY + boxH + 5;
  }

  drawLegend(tiers: ReturnType<typeof buildTierCounts>): void {
    for (const entry of COLOR_LEGEND) {
      const count =
        entry.tier === 'green'
          ? tiers.green
          : entry.tier === 'yellow'
            ? tiers.yellow
            : tiers.red;
      const colors = TIER_PDF[entry.tier];
      const boxH = 18;
      this.ensureSpace(boxH + 3);
      this.pdf.setFillColor(...colors.fill);
      this.pdf.setDrawColor(...colors.border);
      this.pdf.setLineWidth(0.35);
      this.pdf.roundedRect(this.margin, this.y, this.maxWidth, boxH, 1.5, 1.5, 'FD');
      this.pdf.setFontSize(10);
      this.pdf.setFont('helvetica', 'bold');
      this.pdf.setTextColor(...colors.text);
      this.pdf.text(`${entry.label} (${count})`, this.margin + 3, this.y + 6);
      this.pdf.setFont('helvetica', 'normal');
      this.pdf.setFontSize(8);
      this.pdf.setTextColor(51, 65, 85);
      const desc = this.pdf.splitTextToSize(entry.description, this.maxWidth - 8) as string[];
      this.pdf.text(desc[0] ?? '', this.margin + 3, this.y + 11);
      if (desc[1]) this.pdf.text(desc[1], this.margin + 3, this.y + 14.5);
      this.y += boxH + 3;
    }
  }

  drawAttentionItem(item: ParsedComplianceResult): void {
    const tier = getComplianceColorTier(item);
    const colors = TIER_PDF[tier];
    const conf = item.confidence !== null ? `${item.confidence}%` : 'n/a';
    const title = cleanTitle(item.title || `Point ${item.index + 1}`);
    const line = `${title} — ${item.status}, confidence ${conf}`;
    const lines = this.pdf.splitTextToSize(line, this.maxWidth - 8) as string[];
    const boxH = Math.max(10, lines.length * 4.5 + 5);
    this.ensureSpace(boxH + 2);

    this.pdf.setFillColor(...colors.fill);
    this.pdf.setDrawColor(...colors.border);
    this.pdf.setLineWidth(0.35);
    this.pdf.roundedRect(this.margin, this.y, this.maxWidth, boxH, 1.5, 1.5, 'FD');
    this.pdf.setFontSize(9);
    this.pdf.setFont('helvetica', 'bold');
    this.pdf.setTextColor(...colors.text);
    let ty = this.y + 5;
    for (const l of lines) {
      this.pdf.text(l, this.margin + 4, ty);
      ty += 4.5;
    }
    this.y += boxH + 2;
  }

  /** Detailed record card — mirrors web ReferenceComplianceCard layout. */
  drawComplianceRecord(block: ReferenceComplianceBlock): void {
    const tier = referenceBlockToTier(block);
    const colors = TIER_PDF[tier];
    const fulfilledColors = FULFILLED_PDF[tier];
    const pad = 5;
    const innerW = this.maxWidth - pad * 2;
    const cx = this.margin + pad;
    const cardX = this.margin;
    const cardStartY = this.y;
    const cardStartPage = this.pdf.getNumberOfPages();
    const badgeText = referenceBlockBadgeLabel(block);
    const citation = parseReferenceCitation(block.outputResponse);
    const isMissing = /no corresponding procedure found/i.test(
      block.outputResponse,
    );
    const requirementLines = requirementDisplayLines(block.body);
    const cap = block.correctiveAction?.trim();
    const resp = block.responsibility?.trim();

    this.gap(4);
    if (this.y > this.pageHeight - 70) this.newPage();

    this.pdf.setFontSize(7.5);
    this.pdf.setFont('helvetica', 'bold');
    const badgeW = Math.min(innerW, this.pdf.getTextWidth(badgeText) + 12);
    this.pdf.setFillColor(255, 255, 255);
    this.pdf.setDrawColor(...colors.border);
    this.pdf.setLineWidth(0.45);
    this.pdf.roundedRect(cx, this.y, badgeW, 8, 1.2, 1.2, 'FD');
    this.pdf.setTextColor(...colors.text);
    this.pdf.text(badgeText, cx + 5, this.y + 5.5);
    this.y += 11;

    if (block.title) {
      this.writeln(cleanTitle(block.title), 12, {
        bold: true,
        color: colors.text,
        maxWidth: innerW,
        indent: pad,
      });
      this.gap(2);
    }

    if (requirementLines.length > 0) {
      this.drawSectionLabel('Requirement', cx, innerW);
      if (requirementLines.length > 1) {
        for (let i = 0; i < requirementLines.length; i++) {
          const line = requirementLines[i].replace(/^\d+[.)]\s*/, '');
          const numLabel = `${i + 1}.`;
          const fl = this.pdf.splitTextToSize(line, innerW - 16) as string[];
          const fh = fl.length * 4.2 + 5;
          this.ensureSpace(fh + 2);
          this.pdf.setFillColor(255, 255, 255);
          this.pdf.setDrawColor(203, 213, 225);
          this.pdf.setLineWidth(0.25);
          this.pdf.roundedRect(cx, this.y - 1, innerW, fh, 1, 1, 'FD');
          this.pdf.setFontSize(9);
          this.pdf.setFont('helvetica', 'bold');
          this.pdf.setTextColor(71, 85, 105);
          this.pdf.text(numLabel, cx + 3, this.y + 4);
          this.pdf.setFont('helvetica', 'normal');
          this.pdf.setTextColor(30, 41, 59);
          let fy = this.y + 4;
          for (const fline of fl) {
            this.pdf.text(fline, cx + 10, fy);
            fy += 4.2;
          }
          this.y += fh + 2;
        }
      } else {
        this.pdf.setDrawColor(203, 213, 225);
        this.pdf.setLineWidth(0.25);
        const reqLines = this.pdf.splitTextToSize(
          requirementLines[0],
          innerW - 6,
        ) as string[];
        const boxH = reqLines.length * 4.2 + 6;
        this.ensureSpace(boxH + 2);
        this.pdf.setFillColor(255, 255, 255);
        this.pdf.roundedRect(cx, this.y - 1, innerW, boxH, 1, 1, 'FD');
        this.pdf.setFontSize(9);
        this.pdf.setFont('helvetica', 'normal');
        this.pdf.setTextColor(30, 41, 59);
        let ry = this.y + 4;
        for (const line of reqLines) {
          this.pdf.text(line, cx + 3, ry);
          ry += 4.2;
        }
        this.y += boxH + 2;
      }
    }

    if (block.referencePdf) {
      this.drawSectionLabel('Reference PDF', cx, innerW);
      this.writeln(block.referencePdf, 9, {
        bold: true,
        color: [67, 56, 202],
        maxWidth: innerW,
        indent: pad,
      });
    }

    this.drawSectionLabel('Compliance evidence (Output/Response)', cx, innerW);
    const evBoxStartY = this.y;
    let evContentH = 6;
    if (!isMissing && (citation.page || citation.section)) {
      evContentH += 6;
    }
    const quoteText = citation.quote
      ? `"${citation.quote}"`
      : block.outputResponse || '—';
    const quoteLines = this.pdf.splitTextToSize(quoteText, innerW - 14) as string[];
    evContentH += quoteLines.length * 4.3 + 4;
    this.ensureSpace(evContentH + 2);
    this.pdf.setFillColor(...colors.fill);
    this.pdf.setDrawColor(...colors.border);
    this.pdf.setLineWidth(0.4);
    this.pdf.roundedRect(cx, evBoxStartY, innerW, evContentH, 1.5, 1.5, 'FD');
    let ey = evBoxStartY + 5;
    this.pdf.setFontSize(8);
    if (!isMissing && (citation.page || citation.section)) {
      this.pdf.setFont('helvetica', 'bold');
      this.pdf.setTextColor(...colors.text);
      const tags = [
        citation.page ? `Page ${citation.page}` : '',
        citation.section ? `Section ${citation.section}` : '',
      ]
        .filter(Boolean)
        .join('   ·   ');
      this.pdf.text(tags, cx + 4, ey);
      ey += 5;
    }
    this.pdf.setDrawColor(...colors.border);
    this.pdf.setLineWidth(0.8);
    this.pdf.line(cx + 3, ey, cx + 3, ey + quoteLines.length * 4.3 + 1);
    this.pdf.setFontSize(9);
    this.pdf.setFont('helvetica', 'italic');
    this.pdf.setTextColor(30, 41, 59);
    for (const ql of quoteLines) {
      this.pdf.text(ql, cx + 8, ey + 3.5);
      ey += 4.3;
    }
    this.y = evBoxStartY + evContentH + 3;

    const fulfilled = hasDisplayableFulfilledClauses(block.fulfilledClauses)
      ? parseBulletLines(block.fulfilledClauses)
      : [];
    if (fulfilled.length > 0) {
      this.drawSectionLabel('What this reference fulfills', cx, innerW);
      for (let i = 0; i < fulfilled.length; i++) {
        const line = fulfilled[i];
        const numLabel = `${i + 1}.`;
        const fl = this.pdf.splitTextToSize(line, innerW - 18) as string[];
        const fh = fl.length * 4.2 + 6;
        this.ensureSpace(fh + 3);
        this.pdf.setFillColor(...fulfilledColors.fill);
        this.pdf.setDrawColor(...fulfilledColors.border);
        this.pdf.setLineWidth(0.35);
        this.pdf.roundedRect(cx, this.y - 1, innerW, fh, 1, 1, 'FD');
        this.pdf.setFontSize(8.5);
        this.pdf.setFont('helvetica', 'bold');
        this.pdf.setTextColor(...fulfilledColors.text);
        this.pdf.text(`${numLabel}`, cx + 3, this.y + 4);
        this.pdf.setFont('helvetica', 'normal');
        let fy = this.y + 4;
        for (const fline of fl) {
          this.pdf.text(fline, cx + 10, fy);
          fy += 4.2;
        }
        this.y += fh + 3;
      }
    }

    this.gap(2);
    this.drawSectionLabel('Status & confidence', cx, innerW);
    const statBoxH = 16;
    const statBoxW = (innerW - 3) / 2;
    this.ensureSpace(statBoxH + 2);
    const statY = this.y;
    this.pdf.setFillColor(...colors.fill);
    this.pdf.setDrawColor(...colors.border);
    this.pdf.setLineWidth(0.4);
    this.pdf.roundedRect(cx, statY, statBoxW, statBoxH, 1, 1, 'FD');
    this.pdf.roundedRect(cx + statBoxW + 3, statY, statBoxW, statBoxH, 1, 1, 'FD');
    this.pdf.setFontSize(7);
    this.pdf.setFont('helvetica', 'bold');
    this.pdf.setTextColor(71, 85, 105);
    this.pdf.text('Status', cx + 3, statY + 5);
    this.pdf.text('Confidence %', cx + statBoxW + 6, statY + 5);
    this.pdf.setFontSize(10);
    this.pdf.setFont('helvetica', 'bold');
    this.pdf.setTextColor(...colors.text);
    this.pdf.text(block.status || '—', cx + 3, statY + 12);
    this.pdf.text(block.confidence || '—', cx + statBoxW + 6, statY + 12);
    this.y = statY + statBoxH + 4;

    if (cap && cap !== 'N/A' && cap !== '—') {
      this.drawSectionLabel('Corrective Action Plan', cx, innerW);
      const gaps = parseCapGaps(cap);
      if (gaps.length > 0) {
        for (const gap of gaps) {
          this.ensureSpace(14);
          this.pdf.setFillColor(255, 251, 235);
          this.pdf.setDrawColor(245, 158, 11);
          const missingLines = this.pdf.splitTextToSize(
            gap.missing,
            innerW - 10,
          ) as string[];
          const fixLines = gap.fix
            ? (this.pdf.splitTextToSize(gap.fix, innerW - 10) as string[])
            : [];
          const gh =
            8 + missingLines.length * 4.2 + (fixLines.length ? 6 + fixLines.length * 4.2 : 0);
          this.pdf.roundedRect(cx, this.y, innerW, gh, 1, 1, 'FD');
          this.pdf.setFontSize(8.5);
          this.pdf.setFont('helvetica', 'bold');
          this.pdf.setTextColor(180, 83, 9);
          this.pdf.text(`Gap ${gap.index} — Missing`, cx + 3, this.y + 5);
          this.pdf.setFont('helvetica', 'normal');
          this.pdf.setTextColor(120, 53, 15);
          let gy = this.y + 9;
          for (const ml of missingLines) {
            this.pdf.text(ml, cx + 3, gy);
            gy += 4.2;
          }
          if (fixLines.length) {
            gy += 2;
            this.pdf.setFont('helvetica', 'bold');
            this.pdf.setTextColor(146, 64, 14);
            this.pdf.text('Fix:', cx + 3, gy);
            gy += 4.2;
            this.pdf.setFont('helvetica', 'normal');
            for (const fl of fixLines) {
              this.pdf.text(fl, cx + 3, gy);
              gy += 4.2;
            }
          }
          this.y += gh + 3;
        }
      } else {
        this.writeln(cap, 9, { color: [120, 53, 15], maxWidth: innerW, indent: pad });
      }
    }

    if (resp && resp !== 'N/A' && resp !== '—') {
      this.drawSectionLabel('Responsibility', cx, innerW);
      this.writeln(resp, 10, { bold: true, maxWidth: innerW, indent: pad });
    }

    this.gap(3);
    if (this.pdf.getNumberOfPages() === cardStartPage) {
      const cardH = this.y - cardStartY + 4;
      this.pdf.setDrawColor(...colors.border);
      this.pdf.setLineWidth(0.9);
      this.pdf.roundedRect(cardX, cardStartY, this.maxWidth, cardH, 2.5, 2.5, 'S');
      this.y = cardStartY + cardH + 3;
    } else {
      this.gap(3);
    }
  }

  drawResult(item: ParsedComplianceResult): void {
    void item;
  }
}

async function buildPdfDocument(
  report: FullReportExport,
  title: string,
  blocks?: ReferenceComplianceBlock[],
): Promise<import('jspdf').jsPDF> {
  const { jsPDF } = await import('jspdf');
  const pdf = new jsPDF({
    orientation: 'p',
    unit: 'mm',
    format: 'a4',
    compress: true,
  });
  const w = new PdfWriter(pdf);
  const tiers = buildTierCounts(report.results);

  w.writeln(title, 16, { bold: true, color: [30, 41, 59] });
  w.writeln(`Generated ${new Date().toLocaleString()}`, 8, {
    color: [100, 116, 139],
  });
  w.gap(4);

  w.sectionTitle('Color code legend');
  w.drawLegend(tiers);
  w.gap(2);

  w.sectionTitle('Statistics');
  w.writeln(`${report.stats.total} points analyzed`, 9, {
    color: [100, 116, 139],
  });
  w.gap(2);
  w.drawStatusBoxes(
    report.stats.compliant,
    report.stats.partial,
    report.stats.nonCompliant,
  );

  const pdfSummary = summaryForPdf(report.summary);
  if (pdfSummary) {
    w.sectionTitle('Executive summary');
    for (const line of pdfSummary.split('\n')) {
      const trimmed = line.trim();
      if (!trimmed || /^summary$/i.test(trimmed)) continue;
      w.writeln(trimmed, 9);
    }
  }

  w.sectionTitle('Attention focus (< 100% or Partial / Non-Compliant)');
  if (report.stats.attentionItems.length === 0) {
    w.writeln('All points at 100% confidence and Compliant (green).', 9, {
      color: [6, 95, 70],
    });
  } else {
    for (const item of report.stats.attentionItems) {
      w.drawAttentionItem(item);
    }
  }

  w.sectionTitle(`Detailed results (${report.results.length})`);
  if (blocks?.length) {
    for (const block of blocks) {
      w.drawComplianceRecord(block);
    }
  } else {
    for (const item of report.results) {
      w.drawResult(item);
    }
  }

  return pdf;
}

export async function downloadReportPdf(
  report: FullReportExport,
  filename: string,
  title = 'BCP Compliance Gap Analysis Report',
  blocks?: ReferenceComplianceBlock[],
): Promise<void> {
  const pdf = await buildPdfDocument(report, title, blocks);
  pdf.save(filename);
}

export async function downloadComplianceDetailPdf(
  blocks: ReferenceComplianceBlock[],
  stats: FullReportExport['stats'],
  filename: string,
  title: string,
  summary = '',
): Promise<void> {
  const results: ParsedComplianceResult[] = blocks.map((block, index) => {
    const confMatch = block.confidence.match(/(\d+)/);
    return {
      index,
      title: block.title,
      body: block.body,
      fields: [],
      status: block.status,
      confidence: confMatch ? Number(confMatch[1]) : null,
      needsAttention:
        block.status !== 'Compliant' ||
        (confMatch ? Number(confMatch[1]) < 100 : false),
    };
  });
  await downloadReportPdf(
    { summary, results, stats },
    filename,
    title,
    blocks,
  );
}

export async function downloadResultsPdf(
  results: ParsedComplianceResult[],
  filename: string,
): Promise<void> {
  await downloadReportPdf(
    {
      summary: '',
      results,
      stats: {
        total: results.length,
        atFullConfidence: results.filter((r) => r.confidence === 100).length,
        belowFullConfidence: results.filter(
          (r) => r.confidence !== null && r.confidence < 100,
        ).length,
        compliant: results.filter((r) => r.status === 'Compliant').length,
        partial: results.filter((r) => r.status === 'Partial Compliant').length,
        nonCompliant: results.filter((r) => r.status === 'Non-Compliant').length,
        attentionItems: results.filter((r) => r.needsAttention),
      },
    },
    filename,
    'BCP Formatted Compliance Results',
  );
}

export type DualVerifyComparisonPdfItem = {
  pointId: string;
  agreementLabel: string;
  agreementSummary: string;
  pass1Block: ReferenceComplianceBlock;
  pass2Block: ReferenceComplianceBlock;
};

export type DualVerifyAgreementCounts = {
  total: number;
  aligned: number;
  confidence_gap: number;
  status_mismatch: number;
  both_non_compliant: number;
};

/** PDF export comparing Pass 1 (Landing AI) and Pass 2 (LLM) side by side. */
export async function downloadDualVerifyComparisonPdf(params: {
  title: string;
  filename: string;
  summary: string;
  pass1Stats: ReportStats;
  pass2Stats: ReportStats;
  agreementCounts: DualVerifyAgreementCounts;
  items: DualVerifyComparisonPdfItem[];
}): Promise<void> {
  const { jsPDF } = await import('jspdf');
  const pdf = new jsPDF({
    orientation: 'p',
    unit: 'mm',
    format: 'a4',
    compress: true,
  });
  const w = new PdfWriter(pdf);

  w.writeln(params.title, 16, { bold: true, color: [30, 41, 59] });
  w.writeln(`Generated ${new Date().toLocaleString()}`, 8, {
    color: [100, 116, 139],
  });
  w.gap(4);

  w.sectionTitle('Cross-pass agreement');
  w.drawStatusBoxes(
    params.agreementCounts.aligned,
    params.agreementCounts.confidence_gap,
    params.agreementCounts.status_mismatch + params.agreementCounts.both_non_compliant,
  );
  w.writeln(
    `${params.agreementCounts.total} points · Aligned ${params.agreementCounts.aligned} · Confidence gap ${params.agreementCounts.confidence_gap} · Mismatch ${params.agreementCounts.status_mismatch} · Both non-compliant ${params.agreementCounts.both_non_compliant}`,
    8,
    { color: [100, 116, 139] },
  );
  w.gap(2);

  w.sectionTitle('Pass 1 — Landing AI statistics');
  w.writeln(`${params.pass1Stats.total} points analyzed`, 9, {
    color: [100, 116, 139],
  });
  w.drawStatusBoxes(
    params.pass1Stats.compliant,
    params.pass1Stats.partial,
    params.pass1Stats.nonCompliant,
  );

  w.sectionTitle('Pass 2 — LLM verify statistics');
  w.writeln(`${params.pass2Stats.total} points analyzed`, 9, {
    color: [100, 116, 139],
  });
  w.drawStatusBoxes(
    params.pass2Stats.compliant,
    params.pass2Stats.partial,
    params.pass2Stats.nonCompliant,
  );

  const pdfSummary = summaryForPdf(params.summary);
  if (pdfSummary) {
    w.sectionTitle('Executive summary');
    for (const line of pdfSummary.split('\n')) {
      const trimmed = line.trim();
      if (!trimmed) continue;
      w.writeln(trimmed, 9);
    }
  }

  const attentionItems = [
    ...params.pass1Stats.attentionItems.map((item) => ({
      ...item,
      title: `[Pass 1] ${item.title}`,
    })),
    ...params.pass2Stats.attentionItems.map((item) => ({
      ...item,
      title: `[Pass 2] ${item.title}`,
    })),
  ];

  w.sectionTitle('Attention focus (< 100% or Partial / Non-Compliant)');
  if (attentionItems.length === 0) {
    w.writeln('All points at 100% confidence and Compliant on both passes.', 9, {
      color: [6, 95, 70],
    });
  } else {
    for (const item of attentionItems) {
      w.drawAttentionItem(item);
    }
  }

  w.sectionTitle(`Detailed comparison (${params.items.length})`);
  for (const item of params.items) {
    w.sectionTitle(`${item.pointId} — ${item.agreementLabel}`);
    w.writeln(item.agreementSummary, 9, { color: [71, 85, 105] });
    w.gap(2);
    w.writeln('Pass 1 — Landing AI', 11, { bold: true, color: [13, 148, 136] });
    w.drawComplianceRecord(item.pass1Block);
    w.gap(3);
    w.writeln('Pass 2 — LLM verify', 11, { bold: true, color: [79, 70, 229] });
    w.drawComplianceRecord(item.pass2Block);
    w.gap(4);
  }

  pdf.save(params.filename);
}
