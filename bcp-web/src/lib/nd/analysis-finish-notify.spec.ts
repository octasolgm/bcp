import type { AnalysisPoint } from './types';
import {
  analysisFinishSpeech,
  buildAnalysisFinishSummary,
  normalizeFinishSoundMode,
} from './analysis-finish-notify';

function point(id: string, status: string, gapLines: string[]): AnalysisPoint {
  const label = status === 'compliant' ? 'Compliant' : status === 'partial_compliant' ? 'Partial Compliant' : 'Non Compliant';
  const message = [
    id,
    'Clause text',
    '',
    'Document Reference :',
    'Manual.pdf p.3',
    '',
    'Output/Response :',
    '(1) quote',
    '',
    `Comply Yes/No (Status) : ${label}`,
    'Compliance Confidence % : 80%',
    'Gap analysis :',
    gapLines.length ? gapLines.join('\n') : 'N/A',
    'Corrective Action Plan :',
    gapLines.length ? gapLines.map((_, i) => `[${i + 1}] Amend the manual.`).join('\n') : 'N/A',
    'Responsibility :',
    'x',
  ].join('\n');
  return {
    id,
    regulationPointId: `doc:${id}`,
    pointSnapshot: JSON.stringify({ pointNumber: id, pointContent: 'Clause text' }),
    landingAiStatus: 'completed',
    landingAiResult: JSON.stringify({ message }),
    finalStatus: status,
  } as unknown as AnalysisPoint;
}

describe('analysis finish sound', () => {
  it('reads the setting as off unless it is tone or voice', () => {
    expect(normalizeFinishSoundMode('voice')).toBe('voice');
    expect(normalizeFinishSoundMode(' Tone ')).toBe('tone');
    expect(normalizeFinishSoundMode('loud')).toBe('off');
    expect(normalizeFinishSoundMode(null)).toBe('off');
  });

  it('counts statuses and gaps by risk the way the gap page lists them', () => {
    const s = buildAnalysisFinishSummary([
      point('3.4', 'partial_compliant', ['[1] Bearer shares - Missing: no prohibition - Materiality: high']),
      point('3.5', 'partial_compliant', [
        '[1] Funds definition - Missing: not defined - Materiality: low',
        '[2] Timeframe - Missing: not stated - Materiality: medium',
      ]),
      point('3.9', 'compliant', []),
    ]);
    expect(s.clauses).toBe(3);
    expect(s.compliant).toBe(1);
    expect(s.partial).toBe(2);
    expect(s.gaps).toBe(3);
    expect(s.highGaps).toBe(1);
    expect(s.mediumGaps).toBe(1);
    expect(s.lowGaps).toBe(1);
  });

  it('builds a short sentence and leaves out zero counts', () => {
    const text = analysisFinishSpeech({
      clauses: 5, compliant: 1, partial: 4, nonCompliant: 0, notAnalysed: 0,
      gaps: 6, highGaps: 1, mediumGaps: 2, lowGaps: 3,
    });
    expect(text).toBe(
      'Analysis finished. 5 clauses: 1 compliant, 4 partially compliant. 6 gaps: 1 high risk, 2 medium risk, 3 low risk.',
    );
    expect(
      analysisFinishSpeech({
        clauses: 1, compliant: 1, partial: 0, nonCompliant: 0, notAnalysed: 0,
        gaps: 0, highGaps: 0, mediumGaps: 0, lowGaps: 0,
      }),
    ).toBe('Analysis finished. 1 clause: 1 compliant. No gaps found.');
  });
});
