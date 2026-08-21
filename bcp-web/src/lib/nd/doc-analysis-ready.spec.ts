import {
  defaultTargetDateForGapRisk,
  internalAnalysisReadyState,
  regulationAnalysisReadyState,
  usedInAnalysesLabel,
} from './doc-analysis-ready';

describe('doc-analysis-ready', () => {
  it('treats empty manuals as not ready', () => {
    expect(
      regulationAnalysisReadyState({ isManual: true, pointCount: 0, analysisRunCount: 0 }),
    ).toBe('not_ready');
  });

  it('treats extracted unused regulations as ready', () => {
    expect(
      regulationAnalysisReadyState({
        extractionStatus: 'extracted',
        pointCount: 12,
        analysisRunCount: 0,
      }),
    ).toBe('ready');
  });

  it('treats used regulations as analysed', () => {
    expect(
      regulationAnalysisReadyState({
        extractionStatus: 'extracted',
        pointCount: 12,
        analysisRunCount: 3,
      }),
    ).toBe('analysed');
  });

  it('requires parse + extract for internals', () => {
    expect(
      internalAnalysisReadyState({ parseStatus: 'parsed', sectionExtractStatus: 'pending' }),
    ).toBe('not_ready');
    expect(
      internalAnalysisReadyState({
        parseStatus: 'parsed',
        sectionExtractStatus: 'extracted',
        analysisRunCount: 0,
      }),
    ).toBe('ready');
  });

  it('falls back to point count when extraction metadata is absent', () => {
    expect(regulationAnalysisReadyState({ pointCount: 12 })).toBe('ready');
    expect(regulationAnalysisReadyState({ pointCount: 0 })).toBe('not_ready');
  });

  it('does not mark internals unready when parse metadata is absent', () => {
    expect(internalAnalysisReadyState({})).toBe('ready');
    expect(internalAnalysisReadyState({ parseStatus: 'pending' })).toBe('not_ready');
  });

  it('formats used-in-analyses labels', () => {
    expect(usedInAnalysesLabel(0)).toBe('Used in 0 analyses');
    expect(usedInAnalysesLabel(1)).toBe('Used in 1 analysis');
    expect(usedInAnalysesLabel(4)).toBe('Used in 4 analyses');
  });

  it('derives due dates 15 / 30 / 45 from gap risk', () => {
    const from = new Date(2026, 7, 21);
    expect(defaultTargetDateForGapRisk('high', from)).toBe('2026-09-05');
    expect(defaultTargetDateForGapRisk('medium', from)).toBe('2026-09-20');
    expect(defaultTargetDateForGapRisk('low', from)).toBe('2026-10-05');
  });
});
