import { describe, expect, it } from 'vitest';
import {
  gapEvidencePrepStepLabel,
  gapEvidenceRerunUiStatus,
  isGapEvidenceRerunInFlight,
  isPointGapEvidenceRejudging,
  summarizeGapEvidenceRerunProgress,
} from './gap-evidence-activity';
import type { AnalysisPoint } from './types';

describe('gapEvidencePrepStepLabel', () => {
  it('names Azure parse and structural chunking while processing', () => {
    expect(gapEvidencePrepStepLabel('parsing', 'policy.pdf', 'processing')).toMatch(/Azure Document Intelligence/i);
    expect(gapEvidencePrepStepLabel('extracting', 'policy.pdf', 'processing')).toMatch(/Structural chunking/i);
  });
});

describe('isGapEvidenceRerunInFlight', () => {
  it('treats regul pending re-judge as in flight', () => {
    const point = {
      id: 'p1',
      pointSnapshot: '{}',
      landingAiStatus: 'pending',
      finalStatus: null,
    } as AnalysisPoint;
    expect(isPointGapEvidenceRejudging(point)).toBe(true);
    expect(
      isGapEvidenceRerunInFlight({ status: 'completed', regulPipelinePhase: 'done' }, [point], new Set(['p1'])),
    ).toBe(true);
  });

  it('treats run status running as in flight', () => {
    expect(
      isGapEvidenceRerunInFlight({ status: 'running', regulPipelinePhase: 'forward' }, [], null),
    ).toBe(true);
  });
});

describe('summarizeGapEvidenceRerunProgress', () => {
  it('counts queued, running, and done buckets', () => {
    const watch = new Set(['a', 'b', 'c']);
    const points = [
      { id: 'a', pointSnapshot: '{"pointNumber":"1"}', landingAiStatus: 'pending', finalStatus: null },
      { id: 'b', pointSnapshot: '{"pointNumber":"2"}', landingAiStatus: 'running', finalStatus: null },
      { id: 'c', pointSnapshot: '{"pointNumber":"3"}', landingAiStatus: 'completed', finalStatus: 'partial_compliant' },
    ] as import('./types').AnalysisPoint[];
    const counts = summarizeGapEvidenceRerunProgress(points, watch);
    expect(counts.all).toBe(3);
    expect(counts.queued).toBe(1);
    expect(counts.running).toBe(1);
    expect(counts.completed).toBe(1);
    expect(gapEvidenceRerunUiStatus(points[2]!)).toBe('completed');
  });
});
