import {
  aiActionsForGap,
  buildSeededActionPlan,
  buildSeededActionPlansForGap,
  isSeedTemplateActionText,
  summarizeGapForAction,
} from './action-plan-seed';
import { parseRegulElementCapSegments } from './regul-fields';

describe('action plan seed', () => {
  it('splits V5 "[n]" gap lines into one gap per missing requirement', () => {
    const segs = parseRegulElementCapSegments('[1] Funds definition — Missing: a\n[2] Timeframe irrelevant — Missing: b');
    expect(segs).toEqual(['Funds definition — Missing: a', 'Timeframe irrelevant — Missing: b']);
  });

  it('gives each gap every AI action line that carries its number', () => {
    const fix = '[1] Amend Section 4 to include: "x".\n[1] Amend Section 7 to include: "y".\n[2] Amend Section 9 to include: "z".';
    expect(aiActionsForGap({ index: 1, missing: 'm', fix })).toEqual([
      'Amend Section 4 to include: "x".',
      'Amend Section 7 to include: "y".',
    ]);
    const rows = buildSeededActionPlansForGap('p', { index: 2, missing: 'm', fix }, new Date(), { useAiAction: true });
    expect(rows.map((r) => r.actionPlan)).toEqual(['Amend Section 9 to include: "z".']);
    expect(rows[0].gapIndex).toBe(2);
  });

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

  it('recognises untouched sample template actions and nothing else', () => {
    expect(
      isSeedTemplateActionText(
        'Extend the suspicious transaction reporting procedure to address the good-faith protection. Define the escalation path and reporting deadline, and evidence the first reporting cycle.',
      ),
    ).toBeTrue();
    expect(
      isSeedTemplateActionText(
        'Review a sample of reports raised after the change to confirm the good-faith protection is being handled within the stated deadline.',
      ),
    ).toBeTrue();
    expect(
      isSeedTemplateActionText(
        'Amend Section 7.3 to include: "Staff who report a suspicion in good faith are protected from liability."',
      ),
    ).toBeFalse();
    // A template the maker has edited is left alone.
    expect(
      isSeedTemplateActionText(
        'Extend the suspicious transaction reporting procedure to address the good-faith protection.',
      ),
    ).toBeFalse();
    expect(isSeedTemplateActionText('')).toBeFalse();
  });
});
