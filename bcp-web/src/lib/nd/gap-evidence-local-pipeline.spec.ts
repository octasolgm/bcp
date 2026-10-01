import { describe, expect, it } from 'vitest';
import {
  gapEvidenceLocalRowNeverStarted,
  gapEvidenceNeedsManualPrepare,
  gapEvidenceNotStartedDetail,
  gapEvidencePrepStepFromLocalRow,
} from './gap-evidence-local-pipeline';
import type { NdLocalExtractionResult } from '../../app/services/nd/nd-api.service';

describe('gapEvidenceLocalRowNeverStarted', () => {
  it('treats missing row as never started', () => {
    expect(gapEvidenceLocalRowNeverStarted(undefined)).toBe(true);
  });

  it('treats empty statuses as never started', () => {
    const row = { status: '', extractStatus: '', indexStatus: '' } as NdLocalExtractionResult;
    expect(gapEvidenceLocalRowNeverStarted(row)).toBe(true);
    expect(gapEvidencePrepStepFromLocalRow(row)).toBe('not_started');
    expect(gapEvidenceNotStartedDetail(row)).toBe('Not yet parsed or extracted');
  });
});

describe('gapEvidenceNeedsManualPrepare', () => {
  it('requires manual prepare for not_started and failed', () => {
    expect(gapEvidenceNeedsManualPrepare('not_started')).toBe(true);
    expect(gapEvidenceNeedsManualPrepare('failed')).toBe(true);
    expect(gapEvidenceNeedsManualPrepare('parsing')).toBe(false);
    expect(gapEvidenceNeedsManualPrepare('ready')).toBe(false);
  });
});
