import { downloadExcelSheets, type ExcelSheetSpec } from '../../ai-lab/excel-write';
import type { AnalysisPoint } from '../types';
import {
  buildGapAnalysisExportRows,
  gapExportIncludesPhaseColumns,
  type GapAnalysisExcelRow,
} from './gap-analysis-export-rows';
import {
  actionPlanPriorityLabel,
  actionPlanStatusLabel,
  formatActionPlanDate,
  type ActionPlanEntry,
} from '../action-plan';

/** Heading agreed with the client for the regulatory requirement column. */
export const REGULATORY_CLAUSE_HEADER = 'Clause from the regulatory document';
/** Leading column identifying which regulation the clause came from. */
const REGULATION_DOC_HEADER = 'Name of Regulatory Document';

const BASE_COL_WIDTHS = [32, 12, 50, 32, 50, 16, 14, 40, 14];

/**
 * What the export dialog decided to include. Omitting a column list means
 * "every column for that sheet", so existing callers keep the full export.
 */
export type GapAnalysisExportSelection = {
  gapColumns?: string[];
  includeActionPlans?: boolean;
  actionPlanColumns?: string[];
  includeReviews?: boolean;
  reviewColumns?: string[];
};

export type GapAnalysisExcelOptions = {
  /** Regulation document title shown in the first column of every row. */
  regulationDocumentName?: string;
  /** Action plans for the exported run — written to their own sheet. */
  actionPlans?: ActionPlanEntry[];
  /** Point id → clause number, so action plan rows can be traced back to a clause. */
  clauseByPointId?: Map<string, string>;
  selection?: GapAnalysisExportSelection;
};

/** Columns offered by the export dialog for the action plan sheet. */
export const ACTION_PLAN_EXPORT_COLUMNS = [
  REGULATION_DOC_HEADER,
  'Clause #',
  'Action plan',
  'Status',
  'Priority',
  'Target date',
  'Responsibility',
  'Comment',
  'Created by',
  'Created on',
] as const;

/** Columns offered by the export dialog for the reviews sheet. */
export const REVIEW_EXPORT_COLUMNS = [
  REGULATION_DOC_HEADER,
  'Clause #',
  'Action plan',
  'Reviewer',
  'Reviewer role',
  'Review comment',
  'Reviewed on',
] as const;

const ACTION_PLAN_COL_WIDTHS: Record<string, number> = {
  [REGULATION_DOC_HEADER]: 32,
  'Clause #': 12,
  'Action plan': 60,
  Status: 12,
  Priority: 12,
  'Target date': 16,
  Responsibility: 28,
  Comment: 40,
  'Created by': 22,
  'Created on': 16,
};

const REVIEW_COL_WIDTHS: Record<string, number> = {
  [REGULATION_DOC_HEADER]: 32,
  'Clause #': 12,
  'Action plan': 50,
  Reviewer: 24,
  'Reviewer role': 16,
  'Review comment': 60,
  'Reviewed on': 18,
};

/**
 * Drop the columns the dialog unchecked. `wanted === undefined` keeps everything,
 * and an explicitly empty list also keeps everything so a sheet is never blank.
 */
function pickColumns(
  headers: readonly string[],
  rows: string[][],
  wanted: string[] | undefined,
): { headers: string[]; rows: string[][]; keep: number[] } {
  const all = [...headers];
  if (!wanted || !wanted.length) {
    return { headers: all, rows, keep: all.map((_, i) => i) };
  }
  const set = new Set(wanted);
  const keep = all.map((h, i) => (set.has(h) ? i : -1)).filter((i) => i >= 0);
  if (!keep.length) return { headers: all, rows, keep: all.map((_, i) => i) };
  return {
    headers: keep.map((i) => all[i]),
    rows: rows.map((r) => keep.map((i) => r[i] ?? '')),
    keep,
  };
}

function defaultFilename(prefix: string): string {
  return `${prefix}-${new Date().toISOString().slice(0, 10)}.xlsx`;
}

function buildHeaders(includePhases: boolean, requirementHeader: string): string[] {
  const headers = [
    REGULATION_DOC_HEADER,
    'Clause #',
    requirementHeader,
    'Document Reference',
    'Policy Extract',
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

function rowsToMatrix(
  rows: GapAnalysisExcelRow[],
  includePhases: boolean,
  regulationDocumentName: string,
): string[][] {
  return rows.map((r) => {
    const base = [
      regulationDocumentName,
      r.pointNumber,
      r.requirement,
      r.documentReference,
      r.policyExtract || r.policyResponse,
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

/** One row per action plan. Reviews live on their own sheet, not in a cell here. */
function actionPlansSheet(
  plans: ActionPlanEntry[],
  clauseByPointId: Map<string, string>,
  regulationDocumentName: string,
  wanted: string[] | undefined,
): ExcelSheetSpec {
  const rows = plans.map((plan) => [
    regulationDocumentName,
    clauseByPointId.get(plan.analysisPointId) ?? '',
    plan.actionPlan ?? '',
    actionPlanStatusLabel(plan.status),
    actionPlanPriorityLabel(plan.priority),
    formatActionPlanDate(plan.targetDate),
    plan.responsibilityName ?? '',
    plan.comment ?? '',
    plan.createdByName ?? '',
    formatActionPlanDate(plan.createdAt),
  ]);
  const picked = pickColumns(ACTION_PLAN_EXPORT_COLUMNS, rows, wanted);
  return {
    sheetName: 'Action Plans',
    headers: picked.headers,
    rows: picked.rows,
    colWidths: picked.headers.map((h) => ACTION_PLAN_COL_WIDTHS[h] ?? 20),
  };
}

/** One row per review, so a reviewer's comments stay readable and filterable. */
function reviewsSheet(
  plans: ActionPlanEntry[],
  clauseByPointId: Map<string, string>,
  regulationDocumentName: string,
  wanted: string[] | undefined,
): ExcelSheetSpec | null {
  const rows: string[][] = [];
  for (const plan of plans) {
    for (const review of plan.reviews ?? []) {
      rows.push([
        regulationDocumentName,
        clauseByPointId.get(plan.analysisPointId) ?? '',
        plan.actionPlan ?? '',
        review.reviewerName ?? '',
        review.reviewerRole ?? '',
        review.comment ?? '',
        formatActionPlanDate(review.createdAt),
      ]);
    }
  }
  if (!rows.length) return null;
  const picked = pickColumns(REVIEW_EXPORT_COLUMNS, rows, wanted);
  return {
    sheetName: 'Reviews',
    headers: picked.headers,
    rows: picked.rows,
    colWidths: picked.headers.map((h) => REVIEW_COL_WIDTHS[h] ?? 20),
  };
}

function extraSheets(options: GapAnalysisExcelOptions): ExcelSheetSpec[] {
  const plans = options.actionPlans ?? [];
  if (!plans.length) return [];
  const selection = options.selection ?? {};
  const clauses = options.clauseByPointId ?? new Map<string, string>();
  const docName = options.regulationDocumentName ?? '';
  const sheets: ExcelSheetSpec[] = [];

  if (selection.includeActionPlans !== false) {
    sheets.push(actionPlansSheet(plans, clauses, docName, selection.actionPlanColumns));
  }
  if (selection.includeReviews !== false) {
    const sheet = reviewsSheet(plans, clauses, docName, selection.reviewColumns);
    if (sheet) sheets.push(sheet);
  }
  return sheets;
}

const REGUL_GAP_HEADERS = (requirementColumnHeader: string): string[] => [
  REGULATION_DOC_HEADER,
  'Clause No.',
  requirementColumnHeader,
  'Interpretation and expected action (Identified Gaps)',
  'Document Reference',
  'Policy Extract',
  'Compliance Status',
  'Comply Yes/No',
  'Confidence %',
];

/** Column names the export dialog should offer for the Gaps sheet of this run. */
export function gapAnalysisExportColumns(
  points: AnalysisPoint[],
  opts: { regul?: boolean; requirementColumnHeader?: string } = {},
): string[] {
  const header = opts.requirementColumnHeader ?? REGULATORY_CLAUSE_HEADER;
  if (opts.regul) return REGUL_GAP_HEADERS(header);
  const rows = buildGapAnalysisExportRows(points);
  return buildHeaders(gapExportIncludesPhaseColumns(rows), header);
}

/** Regul.ai Book 6 gap sheet — clause no, rule, gaps, document ref, policy extract. */
export async function exportRegulGapAnalysisExcelFromPoints(
  points: AnalysisPoint[],
  filename = defaultFilename('reguliq-gap-analysis'),
  requirementColumnHeader = REGULATORY_CLAUSE_HEADER,
  options: GapAnalysisExcelOptions = {},
): Promise<void> {
  const rows = buildGapAnalysisExportRows(points);
  if (!rows.length) return;
  const docName = options.regulationDocumentName ?? '';
  const headers = REGUL_GAP_HEADERS(requirementColumnHeader);
  const matrix = rows.map((r) => [
    docName,
    r.pointNumber,
    r.requirement,
    r.gapsIdentified,
    r.documentReference,
    r.policyExtract || r.policyResponse,
    r.status,
    r.complyYesNo,
    r.confidence,
  ]);
  const colWidths = [32, 12, 50, 45, 36, 50, 16, 14, 12];
  const picked = pickColumns(headers, matrix, options.selection?.gapColumns);
  await downloadExcelSheets(filename, [
    {
      sheetName: 'Gap Analysis',
      headers: picked.headers,
      rows: picked.rows,
      colWidths: picked.keep.map((i) => colWidths[i] ?? 20),
    },
    ...extraSheets(options),
  ]);
}

/** Client gap analysis Excel (sample layout + confidence, gaps, optional Phase 1/2). */
export async function exportGapAnalysisExcelFromPoints(
  points: AnalysisPoint[],
  filename = defaultFilename('reguliq-gap-analysis'),
  requirementColumnHeader = REGULATORY_CLAUSE_HEADER,
  options: GapAnalysisExcelOptions = {},
): Promise<void> {
  const rows = buildGapAnalysisExportRows(points);
  if (!rows.length) return;
  const includePhases = gapExportIncludesPhaseColumns(rows);
  const colWidths = buildColWidths(includePhases);
  const picked = pickColumns(
    buildHeaders(includePhases, requirementColumnHeader),
    rowsToMatrix(rows, includePhases, options.regulationDocumentName ?? ''),
    options.selection?.gapColumns,
  );
  await downloadExcelSheets(filename, [
    {
      sheetName: 'Gap Analysis',
      headers: picked.headers,
      rows: picked.rows,
      colWidths: picked.keep.map((i) => colWidths[i] ?? 20),
    },
    ...extraSheets(options),
  ]);
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
  /** Regulation document title printed in the header and on each clause. */
  regulationDocumentName?: string;
  actionPlans?: ActionPlanEntry[];
  clauseByPointId?: Map<string, string>;
};

export async function exportGapAnalysisPdfFromPoints(
  points: AnalysisPoint[],
  meta: GapAnalysisExportMeta = {},
): Promise<void> {
  const rows = buildGapAnalysisExportRows(points);
  if (!rows.length) return;
  const includePhases = gapExportIncludesPhaseColumns(rows);
  const plansByClause = new Map<string, ActionPlanEntry[]>();
  for (const plan of meta.actionPlans ?? []) {
    const clause = meta.clauseByPointId?.get(plan.analysisPointId) ?? '';
    const list = plansByClause.get(clause);
    if (list) list.push(plan);
    else plansByClause.set(clause, [plan]);
  }

  // Loaded on demand — jsPDF is large and only PDF export needs it, so keep it out
  // of every route's initial bundle.
  const { jsPDF } = await import('jspdf');
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
  if (meta.regulationDocumentName?.trim()) {
    write(`Name of Regulatory Document: ${meta.regulationDocumentName.trim()}`, 9, true);
  }
  if (meta.subtitle?.trim()) write(meta.subtitle.trim(), 9);
  write(`${rows.length} point(s) · exported ${new Date().toLocaleString()}`, 8);
  y += 4;

  for (const r of rows) {
    ensureSpace(20);
    const titleLine = r.requirement.split('\n')[0]?.trim() ?? '';
    const heading = [r.pointNumber, titleLine].filter(Boolean).join(' — ') || 'Point';
    write(heading, 11, true);
    write(`${REGULATORY_CLAUSE_HEADER}: ${r.pointNumber || '—'}`, 8);
    write(`Status: ${r.status} · Confidence: ${r.confidence} · Comply: ${r.complyYesNo}`, 9);
    if (r.policyExtract?.trim() || r.policyResponse?.trim()) {
      write('Policy extract:', 8, true);
      write((r.policyExtract || r.policyResponse).trim(), 8);
    }
    if (r.documentReference?.trim()) {
      write('Document reference:', 8, true);
      write(r.documentReference.trim(), 8);
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
    const plans = plansByClause.get(r.pointNumber) ?? [];
    if (plans.length) {
      write(`Action plans (${plans.length}):`, 8, true);
      for (const plan of plans) {
        write(
          `• [${actionPlanPriorityLabel(plan.priority)} · ${actionPlanStatusLabel(plan.status)} · target ${formatActionPlanDate(plan.targetDate)} · ${plan.responsibilityName ?? 'Unassigned'}] ${plan.actionPlan}`,
          8,
        );
        for (const review of plan.reviews ?? []) {
          write(`   ↳ Review — ${review.reviewerName ?? 'Reviewer'}: ${review.comment}`, 8);
        }
      }
    }
    y += 5;
  }

  const slug = (meta.runName || 'gap-analysis').replace(/[^a-z0-9]/gi, '_').slice(0, 48);
  doc.save(`${slug}_${new Date().toISOString().slice(0, 10)}.pdf`);
}

/** @deprecated Use exportGapAnalysisPdfFromPoints */
export async function exportGapAnalysisPdf(
  points: AnalysisPoint[],
  meta: GapAnalysisExportMeta = {},
): Promise<void> {
  await exportGapAnalysisPdfFromPoints(points, meta);
}
