import { describe, expect, it } from 'vitest';
import {
  canFinalizeWorkflowRun,
  runReviewPhaseFromStatus,
  runReviewSubmitModeForViewer,
  superAdminWorkflowSubmitTargets,
} from './nd-review-run-helpers';

describe('runReviewPhaseFromStatus', () => {
  it('maps workflow statuses to phases', () => {
    expect(runReviewPhaseFromStatus('submitted_for_review')).toBe('checker');
    expect(runReviewPhaseFromStatus('checker_approved')).toBe('reviewer');
    expect(runReviewPhaseFromStatus('completed')).toBe('maker');
    expect(runReviewPhaseFromStatus('cancelled')).toBe('none');
  });
});

describe('runReviewSubmitModeForViewer', () => {
  it('lets super_admin submit at the current phase', () => {
    expect(runReviewSubmitModeForViewer('checker', 'super_admin')).toBe('checker');
    expect(runReviewSubmitModeForViewer('reviewer', 'super_admin')).toBe('reviewer');
  });

  it('limits maker/checker/reviewer to their phase', () => {
    expect(runReviewSubmitModeForViewer('checker', 'maker')).toBe('none');
    expect(runReviewSubmitModeForViewer('checker', 'checker')).toBe('checker');
    expect(runReviewSubmitModeForViewer('reviewer', 'reviewer')).toBe('reviewer');
  });

  it('uses maker panel mode for super_admin when run has no workflow phase', () => {
    expect(runReviewSubmitModeForViewer('none', 'super_admin')).toBe('maker');
  });
});

describe('superAdminWorkflowSubmitTargets', () => {
  it('offers send-to-checker on completed runs and finalize path on checker_approved', () => {
    const completed = superAdminWorkflowSubmitTargets('completed');
    expect(completed.some((t) => t.action === 'submit')).toBe(true);

    const atChecker = superAdminWorkflowSubmitTargets('submitted_for_review');
    expect(atChecker.some((t) => t.action === 'approve')).toBe(true);

    const atReviewer = superAdminWorkflowSubmitTargets('checker_approved');
    expect(atReviewer.some((t) => t.action === 'pullback_to_checker')).toBe(true);
  });

  it('returns no targets for cancelled runs', () => {
    expect(superAdminWorkflowSubmitTargets('cancelled')).toEqual([]);
  });
});

describe('canFinalizeWorkflowRun', () => {
  it('allows super_admin on checker_approved and reviewer_approved', () => {
    expect(canFinalizeWorkflowRun('super_admin', 'checker_approved')).toBe(true);
    expect(canFinalizeWorkflowRun('super_admin', 'reviewer_approved')).toBe(true);
    expect(canFinalizeWorkflowRun('super_admin', 'completed')).toBe(false);
  });
});
