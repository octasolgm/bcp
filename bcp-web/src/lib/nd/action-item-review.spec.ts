import { describe, expect, it } from 'vitest';
import type { ActionPlanEntry } from './action-plan';
import { countSavedReviewProgress } from './action-item-review';

function plan(overrides: Partial<ActionPlanEntry> & Pick<ActionPlanEntry, 'id' | 'analysisPointId'>): ActionPlanEntry {
  return {
    analysisRunId: 'run-1',
    gapIndex: 1,
    actionPlan: 'Do something',
    status: 'pending',
    priority: 'medium',
    responsibilityType: 'department',
    createdAt: '2026-01-01T00:00:00Z',
    reviews: [],
    reviewCount: 0,
    ...overrides,
  };
}

describe('countSavedReviewProgress', () => {
  it('counts one review slot per action plan when plans exist', () => {
    const pointA = 'point-a';
    const pointB = 'point-b';
    const plans = [
      plan({ id: 'p1', analysisPointId: pointA, gapIndex: 1 }),
      plan({ id: 'p2', analysisPointId: pointA, gapIndex: 1 }),
      plan({ id: 'p3', analysisPointId: pointB, gapIndex: 1 }),
      plan({ id: 'p4', analysisPointId: pointB, gapIndex: 1 }),
    ];
    const progress = countSavedReviewProgress([], [], {}, plans);
    expect(progress).toEqual({ total: 4, reviewed: 0 });
  });

  it('marks every plan on a gap reviewed when that gap has a saved review', () => {
    const pointA = 'point-a';
    const plans = [
      plan({ id: 'p1', analysisPointId: pointA, gapIndex: 1 }),
      plan({ id: 'p2', analysisPointId: pointA, gapIndex: 1 }),
    ];
    const progress = countSavedReviewProgress(
      [],
      [
        {
          id: 'r1',
          analysisPointId: pointA,
          actionIndex: 1,
          status: 'approve',
          createdAt: '2026-01-02T00:00:00Z',
        },
      ],
      {},
      plans,
    );
    expect(progress).toEqual({ total: 2, reviewed: 2 });
  });
});
