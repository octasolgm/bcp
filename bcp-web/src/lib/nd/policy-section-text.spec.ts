import { sanitizePolicySectionText } from './policy-section-text';

describe('sanitizePolicySectionText', () => {
  it('strips Landing parse anchors and page footers', () => {
    const raw = [
      "1 DFSA AML rule 13.3.3 <a id='6ba43595-c105-445c-88cd-4c33b7771376'></a>",
      'Page | 41',
      "<a id='0235ada9-5ca9-4586-bbf3-7ed984aff07d'></a>",
      'Submit all requested information with the SAR.',
    ].join('\n');

    const clean = sanitizePolicySectionText(raw);
    expect(clean).not.toMatch(/<a id/i);
    expect(clean).not.toMatch(/Page\s*\|/);
    expect(clean).toContain('Submit all requested information');
  });
});
