import type { AnalysisPoint } from '../types';
import type { ActionPlanEntry } from '../action-plan';
import type { GapState } from '../gap-state';
import type { GapEvidenceReview } from '../gap-evidence-rerun';
import { buildGapAnalysisExportRows } from './gap-analysis-export-rows';

const NOTE = 'No gap identified at policy level. Detailed procedures are in other documents.';

function point(status: 'compliant' | 'partial_compliant', gap: string, action: string): AnalysisPoint {
  const label = status === 'compliant' ? 'Compliant' : 'Partial Compliant';
  const message = [
    '3.1',
    'Clause text',
    '',
    'Reference PDF :',
    'Manual.pdf - 7.5 p.29',
    '',
    'Document Reference :',
    'Manual.pdf - 7.5 p.29',
    '',
    'Output/Response :',
    '(1) Verbatim policy quote',
    '',
    'Fulfilled clauses :',
    status === 'compliant' ? 'All required elements addressed.' : 'None',
    '',
    `Comply Yes/No (Status) : ${label}`,
    'Compliance Confidence % : 85%',
    'Gap analysis :',
    gap,
    'Corrective Action Plan :',
    action,
    'Responsibility :',
    'N/A',
  ].join('\n');
  return {
    id: `p-${status}`,
    pointSnapshot: JSON.stringify({ pointId: 'doc:3.1', pointNumber: '3.1', pointContent: 'Clause text' }),
    landingAiStatus: 'completed',
    landingAiResult: JSON.stringify({ message }),
    finalStatus: status,
  } as unknown as AnalysisPoint;
}

describe('buildGapAnalysisExportRows - compliant note', () => {
  it('leaves the gaps cell of a compliant row blank when no note is given (other engines)', () => {
    const rows = buildGapAnalysisExportRows([point('compliant', 'N/A', 'N/A')]);
    expect(rows.length).toBe(1);
    expect(rows[0].gapsIdentified).toBe('');
  });

  it('writes the note into the gaps cell of a compliant row when one is given (new analysis page)', () => {
    const rows = buildGapAnalysisExportRows([point('compliant', 'N/A', 'N/A')], { compliantNote: NOTE });
    expect(rows[0].gapsIdentified).toBe(NOTE);
  });

  it('never replaces a real gap on a partial row with the note', () => {
    const rows = buildGapAnalysisExportRows(
      [point('partial_compliant', 'Retention period is missing.', 'Add a retention section.')],
      { compliantNote: NOTE },
    );
    expect(rows[0].gapsIdentified).not.toBe(NOTE);
    expect(rows[0].gapsIdentified).not.toContain('No gap identified at policy level');
  });
});

describe('buildGapAnalysisExportRows - regul hybrid gaps column', () => {
  const gaps =
    '[1] Issuing or dealing in bearer shares - Missing: no prohibition found - Materiality: high\n' +
    '[2] Shell banks - Missing: correspondent accounts not covered - Materiality: low';
  const actions = '[1] Amend 7.6 to include: "Issuing or dealing in bearer shares."\n[2] Amend 7.9 to include shell banks.';
  const POINT_ID = 'p-partial_compliant';
  const plan = (gapIndex: number, actionPlan: string, extra: Partial<ActionPlanEntry> = {}) =>
    ({
      id: `a${gapIndex}-${actionPlan.length}`,
      analysisPointId: POINT_ID,
      gapIndex,
      actionPlan,
      status: 'pending',
      priority: 'medium',
      sortOrder: 0,
      ...extra,
    }) as ActionPlanEntry;
  const gapState = (gapIndex: number, extra: Partial<GapState>) =>
    ({ id: `g${gapIndex}`, analysisRunId: 'r', analysisPointId: POINT_ID, gapIndex, risk: 'medium', riskScore: 50, status: 'pending', ...extra }) as GapState;

  it('lists each gap line with its AI risk and its own action before any action is saved', () => {
    const cell = buildGapAnalysisExportRows([point('partial_compliant', gaps, actions)], { regulHybridGaps: true })[0]
      .gapsIdentified;
    expect(cell).toContain('Gap 1 (Risk High, Pending): Issuing or dealing in bearer shares - Missing: no prohibition found');
    expect(cell).toContain('Action: Amend 7.6 to include');
    expect(cell).toContain('Gap 2 (Risk Low, Pending): Shell banks');
    expect(cell.indexOf('Action: Amend 7.9 to include shell banks.')).toBeGreaterThan(cell.indexOf('Gap 2 ('));
    expect(cell).not.toContain('Missing: [1] Amend');
  });

  it('keeps several AI actions numbered for the same gap under that gap', () => {
    const ai = '[1] Amend 7.6 to add bearer shares.\n[1] Amend 7.9 to add bearer share warrants.\n[2] Amend 7.9 to include shell banks.';
    const cell = buildGapAnalysisExportRows([point('partial_compliant', gaps, ai)], { regulHybridGaps: true })[0]
      .gapsIdentified;
    const gap2 = cell.indexOf('Gap 2 (');
    expect(cell.indexOf('Action: Amend 7.6 to add bearer shares.')).toBeLessThan(gap2);
    expect(cell.indexOf('Action: Amend 7.9 to add bearer share warrants.')).toBeLessThan(gap2);
    expect(cell.indexOf('Action: Amend 7.9 to include shell banks.')).toBeGreaterThan(gap2);
  });

  it('never drops an AI action numbered for no gap', () => {
    const ai = '[1] Amend 7.6.\n[2] Amend 7.9.\n[3] Train staff on the new prohibitions.';
    const cell = buildGapAnalysisExportRows([point('partial_compliant', gaps, ai)], { regulHybridGaps: true })[0]
      .gapsIdentified;
    expect(cell).toContain('Other actions:\n[3] Train staff on the new prohibitions.');
  });

  it('uses the saved actions as shown on the page, with status, priority, due date and owner', () => {
    const saved = [
      plan(1, 'Edited on the page: add bearer shares to the prohibited list.', {
        status: 'resolved',
        priority: 'high',
        responsibilityName: 'Compliance',
      }),
      plan(2, 'Edited: shell bank clause.'),
      plan(2, 'Added by the user: train correspondent banking staff.', { sortOrder: 1, targetDate: '2026-11-15T00:00:00Z' }),
      plan(4, 'Attached to a gap that is no longer listed.'),
      { ...plan(1, 'Another clause action.'), analysisPointId: 'other-point' },
    ];
    const row = buildGapAnalysisExportRows([point('partial_compliant', gaps, actions)], {
      regulHybridGaps: true,
      actionPlans: saved,
    })[0];
    const cell = row.gapsIdentified;
    const gap2 = cell.indexOf('Gap 2 (');
    expect(cell).toContain('Gap 1 (Risk High, Resolved): Issuing or dealing in bearer shares');
    expect(cell).toContain('Action (Resolved, High priority, Compliance): Edited on the page: add bearer shares');
    expect(cell).toContain('Gap 2 (Risk Low, Pending): Shell banks');
    expect(cell.indexOf('Added by the user: train correspondent banking staff.')).toBeGreaterThan(gap2);
    expect(cell).toContain('Action (Pending, Medium priority, due ');
    expect(cell).toContain('Other actions:\nAction (Pending, Medium priority): Attached to a gap that is no longer listed.');
    expect(cell).not.toContain('Amend 7.6');
    expect(cell).not.toContain('Another clause action.');
    expect(row.actionsInGaps).toBe(true);
  });

  it('uses the saved gap risk and resolved state set on the page', () => {
    const states = new Map<string, GapState>([
      [`${POINT_ID}:1`, gapState(1, { risk: 'low', riskScore: 20 })],
      [`${POINT_ID}:2`, gapState(2, { risk: 'high', riskScore: 90, status: 'resolved' })],
    ]);
    const cell = buildGapAnalysisExportRows([point('partial_compliant', gaps, actions)], {
      regulHybridGaps: true,
      gapStates: states,
    })[0].gapsIdentified;
    expect(cell).toContain('Gap 1 (Risk Low, Pending)');
    expect(cell).toContain('Gap 2 (Risk High, Resolved)');
  });

  it('shows the latest "Rerun this gap" result under the gap', () => {
    const reviews = [
      {
        id: 'rev2',
        analysisPointId: POINT_ID,
        status: 'completed',
        gaps: [{ index: 2, outcome: 'partially_fulfilled', remaining: 'nested correspondent accounts' }],
      },
      { id: 'rev1', analysisPointId: POINT_ID, status: 'completed', gaps: [{ index: 2, outcome: 'not_fulfilled' }] },
    ] as unknown as GapEvidenceReview[];
    const cell = buildGapAnalysisExportRows([point('partial_compliant', gaps, actions)], {
      regulHybridGaps: true,
      evidenceReviews: reviews,
    })[0].gapsIdentified;
    expect(cell).toContain('Evidence review: Partly fulfilled; still missing: nested correspondent accounts');
    expect(cell).not.toContain('Not fulfilled');
    expect(cell.indexOf('Evidence review')).toBeGreaterThan(cell.indexOf('Gap 2 ('));
  });

  it('lists the resolved gaps of a clause the page closed automatically, not the compliant note', () => {
    const p = point('partial_compliant', gaps, actions) as AnalysisPoint & Record<string, unknown>;
    p['finalStatus'] = 'compliant';
    p['finalStatusSource'] = 'auto';
    const saved = [plan(1, 'Done: bearer shares added.', { status: 'resolved' }), plan(2, 'Done: shell banks.', { status: 'resolved' })];
    const row = buildGapAnalysisExportRows([p], { regulHybridGaps: true, actionPlans: saved, compliantNote: NOTE })[0];
    expect(row.status.toLowerCase()).toBe('compliant');
    expect(row.gapsIdentified).toContain('Gap 1 (Risk High, Resolved)');
    expect(row.gapsIdentified).toContain('Gap 2 (Risk Low, Resolved)');
    expect(row.gapsIdentified).not.toContain(NOTE);
  });

  it('shows the user\'s edited gaps, as the page does', () => {
    const p = point('partial_compliant', gaps, actions) as AnalysisPoint & Record<string, unknown>;
    p['originalAiActionPlan'] = actions;
    p['finalActionPlan'] = 'Gap(s):\n(1) Missing: Bearer share warrants not prohibited. Fix: Amend 7.6. Priority: High.';
    const cell = buildGapAnalysisExportRows([p], { regulHybridGaps: true })[0].gapsIdentified;
    expect(cell).toContain('Pending): Bearer share warrants not prohibited');
    expect(cell).toContain('Action: Amend 7.6');
    expect(cell).not.toContain('Action: Gap(s)');
    expect(cell).not.toContain('Shell banks');
  });

  it('keeps the older action plan text when the option is off (other pages, demo accounts)', () => {
    const row = buildGapAnalysisExportRows([point('partial_compliant', gaps, actions)], {
      actionPlans: [plan(1, 'Saved action.')],
    })[0];
    expect(row.gapsIdentified).not.toContain('Gap 1 (Risk');
    expect(row.actionsInGaps).toBe(undefined);
  });

  it('keeps the compliant note on compliant rows', () => {
    const rows = buildGapAnalysisExportRows([point('compliant', 'N/A', 'N/A')], {
      regulHybridGaps: true,
      compliantNote: NOTE,
    });
    expect(rows[0].gapsIdentified).toBe(NOTE);
  });
});
