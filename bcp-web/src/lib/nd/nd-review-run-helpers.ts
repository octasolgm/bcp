import { actionItemReviewsToDrafts } from './action-item-review';
import type { ResultsData } from './types';

/** Checker/reviewer/super_admin can add saved reviews during active review stages. */
export function canAddActionItemReviews(
  role: string | null | undefined,
  runStatus: string | null | undefined,
): boolean {
  if (!role || !runStatus) return false;
  if (role === 'super_admin') {
    return runStatus === 'submitted_for_review' || runStatus === 'checker_approved' || runStatus === 'pulled_back';
  }
  if (role === 'checker') return runStatus === 'submitted_for_review' || runStatus === 'pulled_back';
  if (role === 'reviewer') return runStatus === 'checker_approved' || runStatus === 'pulled_back';
  return false;
}

export function reviewWorkspaceLink(
  role: string | null | undefined,
  runId: string,
  runStatus: string | null | undefined,
): string[] | null {
  if (!runId || !runStatus) return null;
  if (
    (role === 'checker' || role === 'super_admin') &&
    runStatus === 'submitted_for_review'
  ) {
    return ['/nd/checker/review', runId];
  }
  if (
    (role === 'reviewer' || role === 'super_admin') &&
    runStatus === 'checker_approved'
  ) {
    return ['/nd/reviewer/review', runId];
  }
  return null;
}

export function isReviewRole(role: string | null | undefined): boolean {
  return role === 'checker' || role === 'reviewer' || role === 'super_admin';
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
  if (role === 'super_admin') {
    if (runStatus === 'pulled_back') return '';
    return 'Gap reviews can be added when the analysis is with checker, reviewer, or pending correction.';
  }
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
