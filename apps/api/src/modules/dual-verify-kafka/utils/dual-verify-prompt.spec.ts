import { describe, expect, it } from 'vitest';
import { buildDualVerifyPrompt } from './dual-verify-prompt';

describe('buildDualVerifyPrompt', () => {
  it('includes Phase 1 reference and gov point text', () => {
    const prompt = buildDualVerifyPrompt(
      { point_id: '2.1', title: 'SCP Approval', text: 'Must approve annually.' },
      'Landing AI pass 1 output here.',
    );
    expect(prompt).toContain('DUAL VERIFICATION PIPELINE');
    expect(prompt).toContain('Landing AI pass 1 output here.');
    expect(prompt).toContain('2.1 SCP Approval');
    expect(prompt).toContain('Must approve annually.');
    expect(prompt).toContain('REFERENCE PDF');
  });
});
