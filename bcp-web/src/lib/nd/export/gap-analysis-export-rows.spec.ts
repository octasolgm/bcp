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
