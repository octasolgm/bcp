import { jsPDF } from 'jspdf';
import { downloadExcelRows } from '../../ai-lab/excel-write';
import {
  DEFAULT_EXCEL_REQUIREMENT_HEADER,
} from '../../ai-lab/export-excel';
import type { AnalysisPoint } from '../types';
import {
  buildGapAnalysisExportRows,
  gapExportIncludesPhaseColumns,
  type GapAnalysisExcelRow,
} from './gap-analysis-export-rows';

const BASE_COL_WIDTHS = [12, 50, 60, 16, 14, 40, 14];

function defaultFilename(prefix: string): string {
  return `${prefix}-${new Date().toISOString().slice(0, 10)}.xlsx`;
}

function buildHeaders(includePhases: boolean, requirementHeader: string): string[] {
  const headers = [
    'Point #',
    requirementHeader,
    'UAE Response / Compliance Level',
    'Status',
    'Comply Yes/No',
    'Gaps Identified',
    'Confidence %',
  ];
  if (includePhases) {
    headers.push(
      'Phase 1 — Status',
      'Phase 1 — Confidence %',
      'Phase 1 — Gaps Identified',
      'Phase 2 — Status',
      'Phase 2 — Confidence %',
      'Phase 2 — Gaps Identified',
    );
  }
  return headers;
}

function buildColWidths(includePhases: boolean): number[] {
  const widths = [...BASE_COL_WIDTHS];
  if (includePhases) widths.push(16, 14, 36, 16, 14, 36);
  return widths;
}

function rowsToMatrix(rows: GapAnalysisExcelRow[], includePhases: boolean): string[][] {
  return rows.map((r) => {
    const base = [
      r.pointNumber,
      r.requirement,
      r.policyResponse,
      r.status,
      r.complyYesNo,
      r.gapsIdentified,
      r.confidence,
    ];
    if (!includePhases) return base;
    return [
      ...base,
      r.phase1?.status ?? '',
      r.phase1?.confidence ?? '',
      r.phase1?.gapsIdentified ?? '',
      r.phase2?.status ?? '',
      r.phase2?.confidence ?? '',
      r.phase2?.gapsIdentified ?? '',
    ];
  });
}

/** Client gap analysis Excel (sample layout + confidence, gaps, optional Phase 1/2). */
export async function exportGapAnalysisExcelFromPoints(
  points: AnalysisPoint[],
  filename = defaultFilename('reguliq-gap-analysis'),
  requirementColumnHeader = DEFAULT_EXCEL_REQUIREMENT_HEADER,
): Promise<void> {
  const rows = buildGapAnalysisExportRows(points);
  if (!rows.length) return;
  const includePhases = gapExportIncludesPhaseColumns(rows);
  await downloadExcelRows(
    filename,
    'Gap Analysis',
    buildHeaders(includePhases, requirementColumnHeader),
    rowsToMatrix(rows, includePhases),
    buildColWidths(includePhases),
  );
}

/** @deprecated Use exportGapAnalysisExcelFromPoints */
export async function exportGapAnalysisExcel(
  points: AnalysisPoint[],
  filename?: string,
): Promise<void> {
  return exportGapAnalysisExcelFromPoints(points, filename);
}

export type GapAnalysisExportMeta = {
  runName?: string;
  subtitle?: string;
};

export function exportGapAnalysisPdfFromPoints(
  points: AnalysisPoint[],
  meta: GapAnalysisExportMeta = {},
): void {
  const rows = buildGapAnalysisExportRows(points);
  if (!rows.length) return;
  const includePhases = gapExportIncludesPhaseColumns(rows);

  const doc = new jsPDF();
  const margin = 14;
  const maxW = 182;
  let y = 18;
  const pageH = doc.internal.pageSize.height;

  const ensureSpace = (need: number) => {
    if (y + need > pageH - 16) {
      doc.addPage();
      y = 18;
    }
  };

  const write = (text: string, size = 9, bold = false) => {
    doc.setFontSize(size);
    doc.setFont('helvetica', bold ? 'bold' : 'normal');
    const lines = doc.splitTextToSize(text, maxW);
    for (const line of lines) {
      ensureSpace(6);
      doc.text(line, margin, y);
      y += size * 0.45 + 2;
    }
  };

  write(meta.runName?.trim() || 'Gap Analysis Report', 14, true);
  if (meta.subtitle?.trim()) write(meta.subtitle.trim(), 9);
  write(`${rows.length} point(s) · exported ${new Date().toLocaleString()}`, 8);
  y += 4;

  for (const r of rows) {
    ensureSpace(20);
    const titleLine = r.requirement.split('\n')[0]?.trim() ?? '';
    const heading = [r.pointNumber, titleLine].filter(Boolean).join(' — ') || 'Point';
    write(heading, 11, true);
    write(`Status: ${r.status} · Confidence: ${r.confidence} · Comply: ${r.complyYesNo}`, 9);
    if (r.policyResponse?.trim()) {
      write('UAE response / compliance level:', 8, true);
      write(r.policyResponse.trim(), 8);
    }
    if (r.gapsIdentified?.trim()) {
      write('Gaps identified:', 8, true);
      write(r.gapsIdentified.trim(), 8);
    }
    if (includePhases && r.phase1) {
      write(
        `Phase 1: ${r.phase1.status} · ${r.phase1.confidence}${r.phase1.gapsIdentified ? '' : ''}`,
        8,
      );
      if (r.phase1.gapsIdentified?.trim()) write(r.phase1.gapsIdentified.trim(), 8);
    }
    if (includePhases && r.phase2) {
      write(`Phase 2: ${r.phase2.status} · ${r.phase2.confidence}`, 8);
      if (r.phase2.gapsIdentified?.trim()) write(r.phase2.gapsIdentified.trim(), 8);
    }
    y += 5;
  }

  const slug = (meta.runName || 'gap-analysis').replace(/[^a-z0-9]/gi, '_').slice(0, 48);
  doc.save(`${slug}_${new Date().toISOString().slice(0, 10)}.pdf`);
}

/** @deprecated Use exportGapAnalysisPdfFromPoints */
export function exportGapAnalysisPdf(
  points: AnalysisPoint[],
  meta: GapAnalysisExportMeta = {},
): void {
  exportGapAnalysisPdfFromPoints(points, meta);
}
