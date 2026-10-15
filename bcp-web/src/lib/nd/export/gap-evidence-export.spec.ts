import type { AnalysisPoint } from '../types';
import type { GapEvidenceReview } from '../gap-evidence-rerun';
import { buildGapAnalysisExportRows } from './gap-analysis-export-rows';
import { evidenceRecheckPdfLines, evidenceRecheckSheets } from './gap-evidence-export';

const clauses = new Map([
  ['p10', '3.10'],
  ['p9', '3.9'],
]);

function review(id: string, pointId: string, at: string, extra: Partial<GapEvidenceReview> = {}): GapEvidenceReview {
  return {
    id,
    rerunId: `run-${id}`,
    analysisPointId: pointId,
    status: 'completed',
    priorFinalStatus: 'partial',
    newFinalStatus: 'compliant',
    evidenceDocuments: [{ id: 'd1', name: 'Board minutes.pdf' }],
    gaps: [
      {
        index: 1,
        text: 'Definition of funds',
        outcome: 'partially_fulfilled',
        covered: 'Funds defined for cash only',
        remaining: 'Digital assets',
        quotes: [{ text: 'Funds means  cash', verified: true, documentName: 'Board minutes.pdf', page: 4 }],
      },
    ],
    actions: [
      {
        actionPlanId: 'a1',
        gapIndex: 1,
        originalText: 'Add the definition of funds',
        outcome: 'partially_fulfilled',
        fulfilledPart: 'Cash covered',
        remainingPart: 'Add digital assets',
        applied: 'split',
      },
    ],
    createdByName: 'Admin',
    createdAt: at,
    completedAt: at,
    ...extra,
  } as GapEvidenceReview;
}

describe('evidence re-check export', () => {
  it('writes a gap sheet and an action sheet, ordered by clause then newest first', () => {
    const sheets = evidenceRecheckSheets(
      [
        review('old', 'p10', '2026-10-01T10:00:00Z'),
        review('new', 'p10', '2026-10-14T10:00:00Z'),
        review('nine', 'p9', '2026-10-05T10:00:00Z'),
        review('failed', 'p9', '2026-10-06T10:00:00Z', { status: 'failed' }),
      ],
      clauses,
      'CBUAE',
    );
    expect(sheets.map((s) => s.sheetName)).toEqual(['Evidence re-check', 'Re-check actions']);

    const [gaps, actions] = sheets;
    expect(gaps.rows.map((r) => r[1])).toEqual(['3.9', '3.10', '3.10']);
    const row = gaps.rows[1];
    expect(row[0]).toBe('CBUAE');
    expect(row[4]).toBe('Partly fulfilled');
    expect(row[5]).toBe('Funds defined for cash only');
    expect(row[6]).toBe('Digital assets');
    expect(row[7]).toBe('"Funds means cash" - Board minutes.pdf · p. 4');
    expect(row[8]).toBe('Board minutes.pdf');
    expect(row[10]).toBe('Compliant');

    expect(actions.rows.length).toBe(3);
    expect(actions.rows[0][7]).toContain('Split');
    expect(actions.rows[0][6]).toBe('Add digital assets');
  });

  it('writes nothing when the run was never re-checked', () => {
    expect(evidenceRecheckSheets([], clauses, 'CBUAE')).toEqual([]);
    expect(evidenceRecheckSheets(undefined, clauses, 'CBUAE')).toEqual([]);
  });

  it('pairs each document reference with its quote in Policy Extract, then lists the new evidence', () => {
    const message = [
      '3.5',
      'Clause text',
      '',
      'Document Reference :',
      'Manual.pdf — section 7.5, p.29',
      'Manual.pdf — section 10, p.33',
      '',
      'Output/Response :',
      '(1) First quote',
      '',
      '(2) Second quote',
      '',
      'Comply Yes/No (Status) : Partial Compliant',
      'Gap analysis :',
      '[1] Definition of funds - Missing: not defined - Materiality: low',
    ].join('\n');
    const point = {
      id: 'p10',
      pointSnapshot: JSON.stringify({ pointNumber: '3.5', pointContent: 'Clause text' }),
      landingAiStatus: 'completed',
      landingAiResult: JSON.stringify({ message }),
      finalStatus: 'partial_compliant',
    } as unknown as AnalysisPoint;
    const cell = buildGapAnalysisExportRows([point], {
      regulHybridGaps: true,
      evidenceReviews: [review('new', 'p10', '2026-10-14T10:00:00Z')],
    })[0].policyExtract;
    expect(cell).toContain('Manual.pdf — section 7.5, p.29\nFirst quote');
    expect(cell).toContain('Manual.pdf — section 10, p.33\nSecond quote');
    expect(cell).not.toContain('(1)');
    expect(cell).toContain('New evidence - Board minutes.pdf');
    expect(cell).toContain('Board minutes.pdf · p. 4\nFunds means  cash');
  });

  it('gives the PDF a heading per re-check with gaps, quotes and actions', () => {
    const text = evidenceRecheckPdfLines([review('new', 'p10', '2026-10-14T10:00:00Z')]).map((l) => l.text);
    expect(text[0]).toContain('Evidence re-check');
    expect(text[0]).toContain('Board minutes.pdf');
    expect(text).toContain('Clause status: Partially compliant -> Compliant');
    expect(text.some((t) => t.startsWith('Gap 1: Partly fulfilled'))).toBeTrue();
    expect(text.some((t) => t.includes('Still missing: Digital assets'))).toBeTrue();
    expect(text.some((t) => t.includes('"Funds means cash"'))).toBeTrue();
    expect(text.some((t) => t.startsWith('Action (Gap 1)'))).toBeTrue();
  });
});
