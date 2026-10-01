import { actionItemReviewsToDrafts } from './action-item-review';
import type { ResultsData } from './types';
import { isPulledBackRun, normalizeRunStatus } from './run-status';

export type WorkflowSubmitAction =
  | 'submit'
  | 'approve'
  | 'pullback'
  | 'finalize'
  | 'pullback_to_checker'
  | 'pullback_to_maker';

export type WorkflowSubmitTarget = {
  role: 'maker' | 'checker' | 'reviewer';
  label: string;
  action: WorkflowSubmitAction;
};

const WORKFLOW_SUBMIT_MAKER: WorkflowSubmitTarget[] = [
  { role: 'checker', label: 'Send to checker', action: 'submit' },
];
const WORKFLOW_SUBMIT_CHECKER: WorkflowSubmitTarget[] = [
  { role: 'reviewer', label: 'Send to reviewer', action: 'approve' },
  { role: 'maker', label: 'Pull back to maker', action: 'pullback' },
];
const WORKFLOW_SUBMIT_REVIEWER: WorkflowSubmitTarget[] = [
  { role: 'checker', label: 'Pull back to checker', action: 'pullback_to_checker' },
  { role: 'maker', label: 'Pull back to maker', action: 'pullback_to_maker' },
];

/** Checker/reviewer/super_admin can add saved reviews during active review stages. */
export function canAddActionItemReviews(
  role: string | null | undefined,
  runStatus: string | null | undefined,
): boolean {
  if (!role || !runStatus) return false;
  if (role === 'super_admin') return true;
  const status = normalizeRunStatus(runStatus);
  if (role === 'checker') return status === 'submitted_for_review' || isPulledBackRun(status);
  if (role === 'reviewer') return status === 'checker_approved' || isPulledBackRun(status);
  return false;
}

export function reviewWorkspaceLink(
  role: string | null | undefined,
  runId: string,
  runStatus: string | null | undefined,
): string[] | null {
  if (!runId || !runStatus) return null;
  const status = normalizeRunStatus(runStatus);
  if (isPulledBackRun(status)) {
    return ['/nd/correction/review', runId];
  }
  if (
    (role === 'checker' || role === 'super_admin') &&
    status === 'submitted_for_review'
  ) {
    return ['/nd/checker/review', runId];
  }
  if (
    (role === 'reviewer' || role === 'super_admin') &&
    status === 'checker_approved'
  ) {
    return ['/nd/reviewer/review', runId];
  }
  return null;
}

export type RunReviewPhase = 'none' | 'maker' | 'checker' | 'reviewer';

/** Which workflow phase the run is in — same for every viewer. */
export function runReviewPhaseFromStatus(runStatus: string | null | undefined): RunReviewPhase {
  const status = normalizeRunStatus(runStatus ?? '');
  if (status === 'submitted_for_review') return 'checker';
  if (status === 'checker_approved') return 'reviewer';
  if (
    status === 'pulled_back' ||
    ['completed', 'dual_verify_failed', 'landing_ai_complete', 'reviewer_approved'].includes(status)
  ) {
    return 'maker';
  }
  return 'none';
}

export function reviewWorkspaceModeForStatus(
  runStatus: string | null | undefined,
): RunReviewPhase {
  switch (normalizeRunStatus(runStatus ?? '')) {
    case 'pulled_back':
      return 'maker';
    case 'submitted_for_review':
      return 'checker';
    case 'checker_approved':
      return 'reviewer';
    default:
      return 'none';
  }
}

/** Who may use the report-level submit panel — super_admin acts at the current phase. */
export function runReviewSubmitModeForViewer(
  phase: RunReviewPhase,
  role: string | null | undefined,
): RunReviewPhase {
  if (phase === 'none') {
    return role === 'super_admin' ? 'maker' : 'none';
  }
  if (role === 'super_admin') return phase;
  if (role === 'reviewer' && phase === 'reviewer') return 'reviewer';
  if (role === 'checker' && phase === 'checker') return 'checker';
  if (role === 'maker' && phase === 'maker') return 'maker';
  return 'none';
}

/** All workflow actions a super admin may take for this run status (deduped). */
export function superAdminWorkflowSubmitTargets(
  runStatus: string | null | undefined,
): WorkflowSubmitTarget[] {
  const status = normalizeRunStatus(runStatus ?? '');
  const phase = runReviewPhaseFromStatus(status);
  const out: WorkflowSubmitTarget[] = [];
  const seen = new Set<WorkflowSubmitAction>();

  const push = (list: WorkflowSubmitTarget[]) => {
    for (const t of list) {
      if (seen.has(t.action)) continue;
      seen.add(t.action);
      out.push(t);
    }
  };

  if (
    phase === 'maker' ||
    ['completed', 'dual_verify_failed', 'landing_ai_complete', 'pulled_back'].includes(status)
  ) {
    push(WORKFLOW_SUBMIT_MAKER);
  }
  if (phase === 'checker' || status === 'submitted_for_review') {
    push(WORKFLOW_SUBMIT_CHECKER);
  }
  if (phase === 'reviewer' || status === 'checker_approved') {
    push(WORKFLOW_SUBMIT_REVIEWER);
  }

  return out;
}

export function workflowSubmitTargetsForViewer(
  phase: RunReviewPhase,
  role: string | null | undefined,
  runStatus: string | null | undefined,
): WorkflowSubmitTarget[] {
  if (role === 'super_admin') {
    return superAdminWorkflowSubmitTargets(runStatus);
  }
  if (phase === 'none') return [];
  if (phase === 'maker') return WORKFLOW_SUBMIT_MAKER;
  if (phase === 'checker') return WORKFLOW_SUBMIT_CHECKER;
  return WORKFLOW_SUBMIT_REVIEWER;
}

export function canFinalizeWorkflowRun(
  role: string | null | undefined,
  runStatus: string | null | undefined,
): boolean {
  const status = normalizeRunStatus(runStatus ?? '');
  if (role === 'super_admin') {
    return status === 'checker_approved' || status === 'reviewer_approved';
  }
  return role === 'reviewer' && status === 'checker_approved';
}

export function isReviewRole(role: string | null | undefined): boolean {
  return role === 'checker' || role === 'reviewer' || role === 'super_admin';
}

const GAP_EVIDENCE_UPLOAD_ROLES = new Set(['super_admin', 'maker', 'checker', 'reviewer']);

/** Report- or point-level gap evidence upload for any ND review role. */
export function canUploadGapEvidence(
  role: string | null | undefined,
  _runStatus?: string | null | undefined,
): boolean {
  if (!role) return false;
  return GAP_EVIDENCE_UPLOAD_ROLES.has(role);
}

export function gapEvidenceUploadDisabledHint(
  role: string | null | undefined,
  _runStatus?: string | null | undefined,
): string {
  if (canUploadGapEvidence(role)) return '';
  if (!role) return 'Sign in to upload gap evidence.';
  return 'Your account role cannot upload gap evidence for this analysis.';
}

export function reviewDisabledHint(
  role: string | null | undefined,
  runStatus: string | null | undefined,
): string {
  if (!role || !runStatus) return '';
  if (canAddActionItemReviews(role, runStatus)) return '';
  if (role === 'checker') {
    if (runStatus === 'checker_approved' || runStatus === 'reviewer_approved') {
      return 'Checker review is complete for this analysis.';
    }
    return 'The maker must submit this analysis for review before you can add gap reviews. Use Pending review when it appears there.';
  }
  if (role === 'reviewer') {
    if (runStatus === 'submitted_for_review') {
      return 'Waiting for checker review first.';
    }
    if (runStatus === 'reviewer_approved') {
      return 'Final review is complete for this analysis.';
    }
    return 'This analysis is not in final review yet.';
  }
  if (role === 'super_admin') return '';
  return '';
}

export function loadPointCommentsFromResults(data: ResultsData): Record<string, string> {
  const comments: Record<string, string> = {};
  for (const c of data.comments ?? []) {
    if (!comments[c.analysisPointId]) {
      comments[c.analysisPointId] = c.comment;
    }
  }
  return comments;
}

export function loadActionItemReviewDraftsFromResults(
  data: ResultsData,
): ReturnType<typeof actionItemReviewsToDrafts> {
  return actionItemReviewsToDrafts(data.actionItemReviews);
}

export function attachmentCountsByPoint(data: ResultsData): Record<string, number> {
  const counts: Record<string, number> = {};
  for (const att of data.pointAttachments ?? []) {
    counts[att.analysisPointId] = (counts[att.analysisPointId] ?? 0) + 1;
  }
  return counts;
}
