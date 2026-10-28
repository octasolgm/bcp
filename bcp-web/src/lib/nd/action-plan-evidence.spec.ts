import { actionPlanCommentWithoutEvidence, actionPlanEvidenceNote } from './action-plan';

describe('actionPlanEvidenceNote', () => {
  it('detects split follow-up actions', () => {
    const note = actionPlanEvidenceNote(
      'Split from resolved action after evidence review (Policy v2.pdf).',
    );
    expect(note?.kind).toBe('split_follow_up');
    expect(note?.shortLabel).toBe('Evidence split');
  });

  it('detects partial fulfillment', () => {
    const note = actionPlanEvidenceNote(
      'Partially fulfilled by AML Policy.pdf: Extend the review cycle to quarterly.',
    );
    expect(note?.kind).toBe('fulfilled_partial');
  });

  it('strips evidence lines from maker comment', () => {
    const rest = actionPlanCommentWithoutEvidence(
      'Maker note here\nPartially fulfilled by doc.pdf: done part',
    );
    expect(rest).toBe('Maker note here');
  });
});
