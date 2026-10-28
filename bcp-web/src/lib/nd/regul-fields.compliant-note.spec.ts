import { v5CompliantScopeNote } from './regul-fields';

describe('v5CompliantScopeNote', () => {
  it('names the single reviewed document', () => {
    const note = v5CompliantScopeNote(['Internal AML Manual']);
    expect(note).toContain('reviewed document: Internal AML Manual.');
    expect(note).toContain('Other documents were not part of this review.');
    expect(note).not.toContain('detailed procedures');
    expect(note.startsWith('No gap identified.')).toBeTrue();
  });

  it('names every reviewed document when there are several', () => {
    const note = v5CompliantScopeNote(['AML Manual', 'KYC Procedure']);
    expect(note).toContain('reviewed documents: AML Manual; KYC Procedure.');
  });

  it('falls back to a generic sentence when no document names are known yet', () => {
    const note = v5CompliantScopeNote([]);
    expect(note).toContain('the internal document(s) selected for this analysis');
    expect(note).not.toContain('undefined');
  });

  it('ignores blank names', () => {
    expect(v5CompliantScopeNote(['  ', ''])).toContain('the internal document(s) selected for this analysis');
  });
});
