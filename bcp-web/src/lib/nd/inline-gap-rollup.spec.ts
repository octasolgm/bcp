import { describe, expect, it } from 'vitest';
import { rollupFromInlineGapItems } from './inline-gap-rollup';

describe('rollupFromInlineGapItems', () => {
  it('counts gaps on non-compliant rows', () => {
    const rollup = rollupFromInlineGapItems([
      { severity: 'compliant', gapCount: 2 },
      { severity: 'partial_compliant', gapCount: 1, gaps: 'x' },
      { severity: 'non_compliant', gaps: 'gap text' },
    ]);
    expect(rollup.gaps).toBe(2);
    expect(rollup.pendingGaps).toBe(2);
    expect(rollup.actions).toBe(2);
  });
});
