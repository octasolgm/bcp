import { extractNumericClauseRef } from '../../gov-point-filter';
import {
  hasDisplayableFulfilledClauses,
  parseBulletLines,
  parseReferenceComplianceBlock,
  resolvePolicyRefAndExtract,
  type ReferenceComplianceBlock,
} from '../../ai-lab/parse-reference-response';
import { formatCorrectiveActionAsGaps } from '../../ai-lab/export-excel';
import { normalizeMultiline } from '../../ai-lab/excel-write';
import type { DualVerifyAgreement } from '../../landing-ai/dual-verify-merge';
import { analysisPointToReportItem } from '../analysis-point-mapper';
import {
  capGapsForAnalysisPoint,
  isVerificationMetaCapText,
  meaningfulCapGaps,
  resolveAiCorrectiveActionForPoint,
} from '../cap-gap-count';
import {
  actionPlanPriorityLabel,
  actionPlansForGap,
  actionPlansForPoint,
  type ActionPlanEntry,
} from '../action-plan';
import { normalizeGapRisk } from '../doc-analysis-ready';
import { deriveGapStatus, gapStateKey, gapStatusLabel, type GapState } from '../gap-state';
import {
  evidenceReviewsForPoint,
  gapEvidenceOutcomeLabel,
  latestGapVerdict,
  type GapEvidenceReview,
} from '../gap-evidence-rerun';
import {
  complianceSeverityLabel,
  resolveAnalysisPointSeverity,
  resolveDisplayConfidence,
  type ComplianceSeverity,
} from '../point-compliance-status';
import type { AnalysisPoint, PointSnapshot } from '../types';
import { parsePointSnapshot, resolveAnalysisPointDisplayNumber } from '../utils';

export type GapAnalysisPhaseExport = {
  status: string;
  confidence: string;
  gapsIdentified: string;
};

export type GapAnalysisExcelRow = {
  /** Clause / point display number (e.g. 3.9 or INT 2-d). */
  pointNumber: string;
  requirement: string;
  /** UAE response column (legacy v8 export). */
  policyResponse: string;
  /** Internal manual location (doc, pages, section). */
  documentReference: string;
  /** Verbatim policy text (separate from document reference). */
  policyExtract: string;
  status: string;
  complyYesNo: string;
  gapsIdentified: string;
  confidence: string;
  phase1?: GapAnalysisPhaseExport;
  phase2?: GapAnalysisPhaseExport;
  /** New analysis page: "What this reference fulfills" lines, as shown on the page (PDF). */
  fulfilled?: string[];
};

function extractMessage(raw?: string | null): string {
  if (!raw?.trim()) return '';
  try {
    const parsed = JSON.parse(raw) as { message?: string };
    return parsed.message?.trim() ?? raw;
  } catch {
    return raw;
  }
}

function parseBlock(message?: string): ReferenceComplianceBlock | null {
  const t = message?.trim() ?? '';
  if (!t) return null;
  return parseReferenceComplianceBlock(t);
}

function normalizeStructuredStatus(status: string): string {
  const s = status.trim().toLowerCase();
  if (!s) return '';
  if (/\bnon[- ]?compliant\b/.test(s) || (/\bnon\b/.test(s) && /compliant/.test(s))) {
    return complianceSeverityLabel('non_compliant');
  }
  if (/\bpartial\b/.test(s)) return complianceSeverityLabel('partial_compliant');
  if (/\bcompliant\b/.test(s)) return complianceSeverityLabel('compliant');
  return status.trim();
}

export function exportStatusLabel(severity: ComplianceSeverity | null): string {
  return severity ? complianceSeverityLabel(severity) : '';
}

function complyYesNoFromSeverity(severity: ComplianceSeverity | null): string {
  if (severity === 'compliant') return 'Yes';
  if (severity === 'partial_compliant') return 'Partial';
  if (severity === 'non_compliant') return 'No';
  return '';
}

function gapsFromCapText(cap: string | null | undefined): string {
  const raw = cap?.trim() ?? '';
  if (!raw || isVerificationMetaCapText(raw)) return '';
  const gaps = meaningfulCapGaps(raw);
  if (gaps.length > 0) {
    return normalizeMultiline(
      gaps
        .map((g) => {
          const parts = [`Gap ${g.index} — Missing: ${g.missing}`];
          if (g.fix?.trim()) parts.push(`Fix: ${g.fix.trim()}`);
          if (g.priority?.trim()) parts.push(`Priority: ${g.priority.trim()}`);
          return parts.join('\n');
        })
        .join('\n\n'),
    );
  }
  return formatCorrectiveActionAsGaps(raw);
}

function gapsFromBlock(block: ReferenceComplianceBlock | null): string {
  if (!block) return '';
  return gapsFromCapText(block.correctiveAction);
}

function gapsForPoint(
  point: AnalysisPoint,
  block: ReferenceComplianceBlock | null,
  agreement: DualVerifyAgreement | undefined,
  compliantNote?: string,
): string {
  const cap =
    point.finalActionPlan?.trim() ||
    point.originalAiActionPlan?.trim() ||
    resolveAiCorrectiveActionForPoint(point) ||
    block?.correctiveAction;
  const fromCap = gapsFromCapText(cap);
  if (fromCap) return fromCap;

  const severity = resolveAnalysisPointSeverity(point);
  if (severity === 'compliant') return compliantNote?.trim() ?? '';

  const fromBlock = gapsFromBlock(block);
  if (fromBlock) return fromBlock;

  if (agreement?.summary?.trim() && agreement.status !== 'aligned') {
    return agreement.summary.trim();
  }

  return '';
}

/** Page state the gap analysis page shows next to the AI result: edits, resolutions, re-check results. */
export type RegulExportPageState = {
  /** The run's saved action plans: a gap is Resolved once all its actions are resolved. */
  actionPlans?: ActionPlanEntry[];
  /** Saved gap rows: risk and Pending / Resolved per gap. */
  gapStates?: Map<string, GapState>;
  /** "Rerun this gap" evidence reviews of the run, newest first (as the page receives them). */
  evidenceReviews?: GapEvidenceReview[];
};

/**
 * Gaps cell for the new analysis page (regul hybrid pipeline), built from the same sources as the gap analysis page:
 * the page's own gap list (the AI's "[n]" gap lines, or the user's edited gaps), each with its risk and Pending /
 * Resolved state (risk saved on the page, else the AI's; resolved once all the gap's saved actions are resolved, or
 * by hand) and the latest "Rerun this gap" result. Gaps only: the actions are on the Actions sheet, where the
 * "Gap #" column links each action to its gap. Empty when the point has no gaps, so the caller keeps the older text.
 */
function regulHybridGapsCell(point: AnalysisPoint, page: RegulExportPageState): string {
  const gaps = capGapsForAnalysisPoint(point, true).filter((g) => g.missing.trim());
  if (!gaps.length) return '';
  const saved = page.actionPlans?.length ? actionPlansForPoint(page.actionPlans, point.id) : [];
  const reviews = evidenceReviewsForPoint(page.evidenceReviews, point.id);
  const blocks = gaps.map((gap) => {
    const state = page.gapStates?.get(gapStateKey(point.id, gap.index)) ?? null;
    const risk = actionPlanPriorityLabel(state?.risk ?? normalizeGapRisk(gap.priority));
    const status = gapStatusLabel(deriveGapStatus(actionPlansForGap(saved, gap.index), state));
    const lines = [`Gap ${gap.index} (Risk ${risk}, ${status}): ${gap.missing.trim()}`];
    const verdict = latestGapVerdict(reviews, gap.index);
    if (verdict) {
      const remaining = verdict.gap.remaining?.trim();
      lines.push(
        `Evidence review: ${gapEvidenceOutcomeLabel(verdict.gap.outcome)}` +
          (remaining && verdict.gap.outcome !== 'fulfilled' ? `; still missing: ${remaining}` : ''),
      );
    }
    return lines.join('\n');
  });
  return normalizeMultiline(blocks.join('\n\n'));
}

function requirementCell(point: AnalysisPoint, landing: ReferenceComplianceBlock | null): string {
  const snap = parsePointSnapshot(point.pointSnapshot);
  const title = snap.pointTitle?.trim() || landing?.title?.trim() || '';
  const body = snap.pointContent?.trim() || landing?.body?.trim() || '';
  if (title && body) return normalizeMultiline(`${title}\n\n${body}`);
  return normalizeMultiline(title || body || '');
}

function policyCell(block: ReferenceComplianceBlock | null, llmRaw?: string): string {
  if (block?.outputResponse?.trim()) return normalizeMultiline(block.outputResponse.trim());
  if (block?.fulfilledClauses?.trim()) return normalizeMultiline(block.fulfilledClauses.trim());
  const raw = llmRaw?.trim() ?? '';
  if (raw) return normalizeMultiline(raw.length > 4000 ? `${raw.slice(0, 4000)}…` : raw);
  return '';
}

function isGenericDocReference(ref: string): boolean {
  const s = ref.trim().toLowerCase();
  return !s || s === 'internal policy manual' || s === 'n/a' || s === '—';
}

function phaseExport(block: ReferenceComplianceBlock | null): GapAnalysisPhaseExport {
  const status = normalizeStructuredStatus(block?.status ?? '');
  const confidence = block?.confidence?.trim() || '—';
  const gaps = gapsFromBlock(block);
  return {
    status: status || '—',
    confidence,
    gapsIdentified: gaps,
  };
}

function comparePointIds(a: string, b: string): number {
  return a.localeCompare(b, undefined, { numeric: true });
}

/** Regul forward formatter puts clause no on the first line of the landing message (block title). */
function clauseNumberFromReferenceBlock(block: ReferenceComplianceBlock | null): string {
  if (!block?.title?.trim()) return '';
  const line = block.title.trim().split('\n')[0].trim();
  if (!line) return '';
  if (/^INT\s+/i.test(line)) return line;
  if (/^\d+(?:\.\d+)*$/.test(line)) return line;
  return extractNumericClauseRef(line) ?? '';
}

function clauseNumberFromMessageHead(message?: string | null): string {
  const first = (message ?? '').trim().split('\n').find((l) => l.trim())?.trim() ?? '';
  if (!first) return '';
  if (/^INT\s+/i.test(first)) return first;
  if (/^\d+(?:\.\d+)*$/.test(first)) return first;
  return extractNumericClauseRef(first) ?? '';
}

function resolveExportClauseNumber(
  point: AnalysisPoint,
  snap: PointSnapshot,
  landingMsg: string,
  landingBlock: ReferenceComplianceBlock | null,
  llmBlock: ReferenceComplianceBlock | null,
): string {
  return (
    resolveAnalysisPointDisplayNumber(point, snap) ||
    clauseNumberFromReferenceBlock(landingBlock) ||
    clauseNumberFromReferenceBlock(llmBlock) ||
    clauseNumberFromMessageHead(landingMsg) ||
    clauseNumberFromMessageHead(extractMessage(point.googleAiResult)) ||
    ''
  );
}

/** Build client-style gap analysis Excel rows from ND analysis points. */
export function buildGapAnalysisExportRows(
  points: AnalysisPoint[],
  options: {
    /** Written in the gaps column of compliant rows (new analysis page only). */
    compliantNote?: string;
    /** New analysis page (regul hybrid pipeline): gaps column lists the page's gaps with their actions. */
    regulHybridGaps?: boolean;
  } & RegulExportPageState = {},
): GapAnalysisExcelRow[] {
  const keyed: { key: string; row: GapAnalysisExcelRow }[] = [];
  const seen = new Set<string>();

  for (const point of points) {
    if (seen.has(point.id)) continue;
    const report = analysisPointToReportItem(point);
    if (!report) continue;
    const landingMsg = extractMessage(point.landingAiResult);
    const llmMsg = extractMessage(point.googleAiResult);
    if (!landingMsg && !llmMsg) continue;
    seen.add(point.id);

    const snap = parsePointSnapshot(point.pointSnapshot);
    const landingBlock = parseBlock(landingMsg);
    const llmBlock = parseBlock(llmMsg);
    const pointNumber = resolveExportClauseNumber(point, snap, landingMsg, landingBlock, llmBlock);
    const sortKey = pointNumber || point.regulationPointId || point.id;

    const structured =
      landingBlock?.outputResponse?.trim()
        ? landingBlock
        : llmBlock?.outputResponse?.trim()
          ? llmBlock
          : landingBlock ?? llmBlock;

    const { documentReference, policyExtract } = resolvePolicyRefAndExtract(
      landingBlock,
      llmBlock,
    );

    const severity = resolveAnalysisPointSeverity(point);
    // A clause the page closed (every gap resolved, status set to compliant automatically) still lists its
    // resolved gaps on the page, so the export lists them too instead of the compliant note.
    const hybridGaps =
      !!options.regulHybridGaps &&
      (severity !== 'compliant' || (point.finalStatusSource ?? '').toLowerCase() === 'auto');
    const hybridCell = hybridGaps ? regulHybridGapsCell(point, options) : '';
    const hasPhase2 = Boolean(llmMsg.trim());

    const row: GapAnalysisExcelRow = {
      pointNumber,
      requirement: requirementCell(point, landingBlock),
      policyResponse: policyCell(structured, llmMsg),
      documentReference:
        documentReference && !isGenericDocReference(documentReference)
          ? normalizeMultiline(documentReference)
          : '',
      policyExtract: policyExtract ? normalizeMultiline(policyExtract) : '',
      status: exportStatusLabel(severity),
      complyYesNo: complyYesNoFromSeverity(severity),
      gapsIdentified:
        hybridCell ||
        gapsForPoint(point, structured, report.agreement, options.compliantNote),
      confidence: resolveDisplayConfidence(point),
    };
    if (options.regulHybridGaps) {
      const fulfilled = landingBlock?.fulfilledClauses ?? '';
      row.fulfilled = hasDisplayableFulfilledClauses(fulfilled) ? parseBulletLines(fulfilled) : [];
    }

    if (landingMsg.trim()) row.phase1 = phaseExport(landingBlock);
    if (hasPhase2) row.phase2 = phaseExport(llmBlock);

    keyed.push({ key: sortKey, row });
  }

  return keyed.sort((a, b) => comparePointIds(a.key, b.key)).map((k) => k.row);
}

export function gapExportIncludesPhaseColumns(rows: GapAnalysisExcelRow[]): boolean {
  return rows.some((r) => Boolean(r.phase1 && r.phase2));
}