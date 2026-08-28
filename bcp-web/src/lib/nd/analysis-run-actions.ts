import type { AnalysisRunSummary } from './types';
import { analysisRunNeedsExecutionView, isLegacyAnalysisRun } from './run-links';

export function canSendRunForReview(
  run: AnalysisRunSummary,
  role: string | null | undefined,
): boolean {
  if (analysisRunNeedsExecutionView(run)) return false;
  if (isLegacyAnalysisRun(run)) return false;
  if (role !== 'maker' && role !== 'super_admin') return false;
  return ['completed', 'dual_verify_failed', 'landing_ai_complete', 'pulled_back'].includes(
    (run.status ?? '').toLowerCase(),
  );
}

/** Whether the current role can pull a run back to itself before the next role has acted on it. */
export function canRecallRun(
  run: AnalysisRunSummary,
  role: string | null | undefined,
  profileId?: string | null,
): boolean {
  if (isLegacyAnalysisRun(run)) return false;
  const status = (run.status ?? '').toLowerCase();
  if (status === 'submitted_for_review') {
    if (role === 'maker') return !run.createdBy || run.createdBy === profileId;
    return role === 'super_admin';
  }
  if (status === 'checker_approved') {
    return role === 'checker' || role === 'super_admin';
  }
  return false;
}

export function canReviewRun(
  run: AnalysisRunSummary,
  role: string | null | undefined,
): boolean {
  if (isLegacyAnalysisRun(run)) return false;
  const status = (run.status ?? '').toLowerCase();
  if (role === 'checker' && status === 'submitted_for_review') return true;
  if (role === 'reviewer' && status === 'checker_approved') return true;
  return false;
}

export function canEditRunPlans(
  run: AnalysisRunSummary,
  role: string | null | undefined,
): boolean {
  if (role !== 'maker' && role !== 'super_admin') return false;
  if (analysisRunNeedsExecutionView(run)) return false;
  if (isLegacyAnalysisRun(run)) return false;
  return [
    'completed',
    'submitted_for_review',
    'checker_approved',
    'reviewer_approved',
    'pulled_back',
    'dual_verify_failed',
    'landing_ai_complete',
  ].includes((run.status ?? '').toLowerCase());
}

export function canDeleteRun(
  run: AnalysisRunSummary,
  role: string | null | undefined,
  profileId?: string | null,
): boolean {
  if (role === 'super_admin') return true;
  if (role === 'maker') {
    return isLegacyAnalysisRun(run) || !run.createdBy || run.createdBy === profileId;
  }
  return false;
}

export function runViewActionLabel(
  run: AnalysisRunSummary,
  role: string | null | undefined,
  opts?: { queueReview?: boolean; viewOnly?: boolean },
): string {
  if (opts?.queueReview && !opts.viewOnly) return 'Review';
  if (canReviewRun(run, role)) return 'Review';
  if (analysisRunNeedsExecutionView(run)) return 'Continue';
  return 'View';
}

export function submitRunActionLabel(run: AnalysisRunSummary): string {
  return (run.status ?? '').toLowerCase() === 'pulled_back' ? 'Resubmit' : 'Submit';
}
