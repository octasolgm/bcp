import type { AnalysisRunSummary } from './types';
import { isRegulWorkflow } from './regul-fields';
import { analysisRunNeedsExecutionView } from './run-links';

export function normalizeRunStatus(status: string): string {
  return (status ?? '').trim().toLowerCase();
}

/** Which role currently owns the analysis in the review workflow. */
export type AnalysisRunCurrentRole = 'maker' | 'checker' | 'reviewer' | 'complete';

export function analysisRunCurrentRole(status: string): AnalysisRunCurrentRole | null {
  const s = normalizeRunStatus(status);
  switch (s) {
    case 'submitted_for_review':
      return 'checker';
    case 'checker_approved':
      return 'reviewer';
    case 'reviewer_approved':
      return 'complete';
    case 'pulled_back':
    case 'completed':
    case 'dual_verify_failed':
    case 'landing_ai_complete':
    case 'draft':
    case 'running':
    case 'processing':
    case 'queued':
    case 'failed':
      return 'maker';
    default:
      return null;
  }
}

export function analysisRunCurrentRoleLabel(role: AnalysisRunCurrentRole | null): string {
  switch (role) {
    case 'maker':
      return 'Maker';
    case 'checker':
      return 'Checker';
    case 'reviewer':
      return 'Reviewer';
    case 'complete':
      return 'Complete';
    default:
      return '—';
  }
}

/** Display name for who created / submitted the analysis. */
export function analysisRunSubmittedByLabel(run: AnalysisRunSummary): string {
  const name = run.makerName?.trim();
  return name || 'Unknown';
}

export function analysisRunSubmittedDate(run: AnalysisRunSummary): string {
  return run.submittedAt ?? run.submittedToCheckerAt ?? run.createdAt;
}

export function analysisRunSubmittedByCaption(run: AnalysisRunSummary): string {
  const s = normalizeRunStatus(run.status);
  if (['submitted_for_review', 'checker_approved', 'reviewer_approved'].includes(s)) {
    return 'Submitted by';
  }
  if (s === 'pulled_back') return 'Returned to';
  return 'Created by';
}

/** Run finished analysis but maker has not submitted for checker review yet. */
export function isAnalysisRunSubmitReviewPending(status: string): boolean {
  return ['completed', 'dual_verify_failed', 'landing_ai_complete'].includes(normalizeRunStatus(status));
}

/** Analysis has produced its findings — safe to export, seed actions, or review. */
export function isAnalysisRunResultsReady(status: string): boolean {
  return [
    'completed',
    'dual_verify_failed',
    'landing_ai_complete',
    'submitted_for_review',
    'pulled_back',
    'checker_approved',
    'reviewer_approved',
  ].includes(normalizeRunStatus(status));
}

/** Human-readable workflow / run status label for badges and lists. */
export function analysisRunStatusLabel(status: string): string {
  const s = normalizeRunStatus(status);
  const labels: Record<string, string> = {
    draft: 'Draft',
    running: 'Running',
    processing: 'Processing',
    queued: 'Queued',
    failed: 'Failed',
    cancelled: 'Cancelled',
    completed: 'Completed',
    dual_verify_failed: 'Dual verify failed',
    landing_ai_complete: 'Analysis complete',
    submitted_for_review: 'Submitted for review',
    pulled_back: 'Pulled back',
    checker_approved: 'Checker approved',
    reviewer_approved: 'Review complete',
    deleted: 'Deleted',
  };
  return labels[s] ?? s.replace(/_/g, ' ').replace(/\b\w/g, (c) => c.toUpperCase());
}

/** Status label shown on run rows/badges (may differ from raw DB status). */
export function analysisRunDisplayStatusLabel(status: string): string {
  if (isAnalysisRunSubmitReviewPending(status)) return 'Submit for review pending';
  return analysisRunStatusLabel(status);
}

/** Next workflow step or in-progress hint for a run row. */
export function analysisRunWorkflowLabel(run: AnalysisRunSummary): string {
  // Once a run has left the maker's desk, the workflow label reflects who currently
  // owns it. This must take priority over the regul pipeline phase below — otherwise
  // a regul run that finished its pipeline (phase 'done') keeps showing "Finalizing"
  // even after it has been submitted for review, which disagrees with the "With
  // checker"/"With reviewer" label shown once the run is opened from the queue.
  const s = normalizeRunStatus(run.status);
  if (isAnalysisRunSubmitReviewPending(s)) return 'Submit for review pending';
  if (s === 'pulled_back') return 'Resubmit pending';
  if (s === 'submitted_for_review') return 'With checker';
  if (s === 'checker_approved') return 'With reviewer';
  if (s === 'reviewer_approved') return 'Review complete';

  if (isRegulWorkflow(run.workflowEngine)) {
    const phase = (run.regulPipelinePhase ?? '').toLowerCase();
    if (phase === 'forward') return 'Forward judgment';
    if (phase === 'reverse') return 'Reverse coverage mapping';
    if (phase === 'qualitative') return 'Qualitative assessment';
    if (phase === 'done') return 'Finalizing';
    if (analysisRunNeedsExecutionView(run)) return 'Regul pipeline';
  }

  if (analysisRunNeedsExecutionView(run)) {
    const total = run.totalPointsCount ?? 0;
    const processed = run.processedPointsCount ?? 0;
    if (total > 0 && processed < total) return `${processed}/${total} points processed`;
    if ((run.dualVerifyFailedCount ?? 0) > 0) return 'Rerun failed points';
    return 'Continue analysis';
  }

  if (run.workflowHolder?.trim()) return run.workflowHolder.trim();
  return '';
}

export function sortAnalysisRunsByRecent(runs: AnalysisRunSummary[]): AnalysisRunSummary[] {
  return [...runs].sort((a, b) => Date.parse(b.createdAt) - Date.parse(a.createdAt));
}

export function analysisRunPointsLabel(run: AnalysisRunSummary): string {
  const total = run.totalPointsCount ?? 0;
  const processed = run.processedPointsCount ?? 0;
  if (!total) return '—';
  return `${processed}/${total} points`;
}

export interface AnalysisRunComplianceBreakdown {
  compliant: number;
  partial: number;
  nonCompliant: number;
}

/** Compliant/partial/non-compliant point counts for a run's Summary column. */
export function analysisRunComplianceBreakdown(run: AnalysisRunSummary): AnalysisRunComplianceBreakdown | null {
  const { compliant, partial, nonCompliant } = run;
  if (compliant == null && partial == null && nonCompliant == null) return null;
  return { compliant: compliant ?? 0, partial: partial ?? 0, nonCompliant: nonCompliant ?? 0 };
}
