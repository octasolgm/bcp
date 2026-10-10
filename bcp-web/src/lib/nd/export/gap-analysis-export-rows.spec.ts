import type { AnalysisPoint } from '../types';
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

  it('lists each gap line with its own action', () => {
    const rows = buildGapAnalysisExportRows([point('partial_compliant', gaps, actions)], { regulHybridGaps: true });
    const cell = rows[0].gapsIdentified;
    expect(cell).toContain('Gap 1: Issuing or dealing in bearer shares - Missing: no prohibition found');
    expect(cell).toContain('Action: Amend 7.6 to include');
    expect(cell).toContain('Gap 2: Shell banks');
    expect(cell).toContain('Action: Amend 7.9 to include shell banks.');
    expect(cell.indexOf('Amend 7.9')).toBeGreaterThan(cell.indexOf('Gap 2:'));
    expect(cell).not.toContain('Missing: [1] Amend');
  });

  it('keeps several actions numbered for the same gap under that gap', () => {
    const plan = '[1] Amend 7.6 to add bearer shares.\n[1] Amend 7.9 to add bearer share warrants.\n[2] Amend 7.9 to include shell banks.';
    const cell = buildGapAnalysisExportRows([point('partial_compliant', gaps, plan)], { regulHybridGaps: true })[0]
      .gapsIdentified;
    const gap2 = cell.indexOf('Gap 2:');
    expect(cell.indexOf('Action: Amend 7.6 to add bearer shares.')).toBeLessThan(gap2);
    expect(cell.indexOf('Action: Amend 7.9 to add bearer share warrants.')).toBeLessThan(gap2);
    expect(cell.indexOf('Action: Amend 7.9 to include shell banks.')).toBeGreaterThan(gap2);
  });

  it('never drops an action numbered for no gap', () => {
    const plan = '[1] Amend 7.6.\n[2] Amend 7.9.\n[3] Train staff on the new prohibitions.';
    const cell = buildGapAnalysisExportRows([point('partial_compliant', gaps, plan)], { regulHybridGaps: true })[0]
      .gapsIdentified;
    expect(cell).toContain('Other actions:\n[3] Train staff on the new prohibitions.');
  });

  it('keeps the older action plan text when the option is off (other pages, demo accounts)', () => {
    const rows = buildGapAnalysisExportRows([point('partial_compliant', gaps, actions)]);
    expect(rows[0].gapsIdentified).not.toContain('Gap 1: Issuing');
  });

  it('keeps the compliant note on compliant rows', () => {
    const rows = buildGapAnalysisExportRows([point('compliant', 'N/A', 'N/A')], {
      regulHybridGaps: true,
      compliantNote: NOTE,
    });
    expect(rows[0].gapsIdentified).toBe(NOTE);
  });
});
