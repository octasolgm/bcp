import { buildSeededActionPlan, summarizeGapForAction } from './action-plan-seed';

describe('action plan seed', () => {
  it('summarizes a gap into a phrase that reads mid-sentence', () => {
    const text = summarizeGapForAction({
      index: 1,
      missing: 'No equivalent internal procedure covers — sanctions screening at onboarding. Further detail follows.',
      fix: '',
      priority: 'higher',
    });
    expect(text.startsWith('No ')).toBe(false);
    expect(text).toContain('sanctions screening at onboarding');
  });

  it('picks the catalog owner and template matching the gap topic', () => {
    const seeded = buildSeededActionPlan('point-1', {
      index: 2,
      missing: 'Staff training on suspicious transaction reporting is not covered',
      fix: '',
      priority: 'medium',
    });
    expect(seeded.analysisPointId).toBe('point-1');
    expect(seeded.gapIndex).toBe(2);
    expect(seeded.ownerLabel).toBe('Human Resources');
    expect(seeded.actionPlan).toContain('annual AML/CFT training');
  });

  it('falls back to a generic policy action when nothing matches', () => {
    const seeded = buildSeededActionPlan('point-2', {
      index: 1,
      missing: 'The clause expectation is not reflected anywhere',
      fix: '',
      priority: 'low',
    });
    expect(seeded.ownerLabel).toBe('Compliance');
    expect(seeded.actionPlan).toContain('Update the internal policy');
  });

  it('derives the target date from gap risk: high 15, medium 30, low 45 days', () => {
    const from = new Date('2026-01-01T00:00:00Z');
    const forRisk = (priority: string) =>
      buildSeededActionPlan('p', { index: 1, missing: 'x', fix: '', priority }, from).targetDate;

    expect(forRisk('higher')).toBe('2026-01-16');
    expect(forRisk('medium')).toBe('2026-01-31');
    expect(forRisk('low')).toBe('2026-02-15');
  });
});
