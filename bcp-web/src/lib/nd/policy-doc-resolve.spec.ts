import {
  buildPolicyRefProofs,
  parsePolicyCitationFromLine,
} from './policy-doc-resolve';

describe('parsePolicyCitationFromLine', () => {
  it('parses bracketed document name with section and page', () => {
    const parsed = parsePolicyCitationFromLine(
      '[Implementation Manual.pdf], Section 7.28, Page 10: "STR confidentiality"',
    );
    expect(parsed.docName).toBe('Implementation Manual.pdf');
    expect(parsed.section).toBe('7.28');
    expect(parsed.page).toBe('10');
    expect(parsed.quote).toBe('STR confidentiality');
  });
});

describe('buildPolicyRefProofs', () => {
  const catalog = [
    {
      id: 'doc-a',
      originalFileName: 'Implementation Manual.pdf',
    },
    {
      id: 'doc-b',
      originalFileName: 'Branch Manual.pdf',
    },
  ];

  it('collects multiple source refs from output and fulfilled clauses', () => {
    const landing = `3.2 Confidentiality

LFIs must keep STR information confidential.

Reference PDF :
Implementation Manual.pdf, Branch Manual.pdf

Output/Response :
[Implementation Manual.pdf], Section 7.28, Page 10: "confidentiality rule"
[Branch Manual.pdf], Section 4.1, Page 25: "tipping off prohibition"

Fulfilled clauses :
• STR confidentiality — [Implementation Manual.pdf], Section 7.28, Page 10: "confidentiality rule"
• Tipping off — [Branch Manual.pdf], Section 4.1, Page 25: "tipping off prohibition"

Comply Yes/No (Status) : Compliant
Compliance Confidence % : 92%
Corrective Action Plan :

Responsibility :
`;

    const refs = buildPolicyRefProofs(landing, '', catalog);
    expect(refs.length).toBeGreaterThanOrEqual(2);
    expect(refs.some((r) => r.docId === 'doc-a' && r.page === '10')).toBe(true);
    expect(refs.some((r) => r.docId === 'doc-b' && r.page === '25')).toBe(true);
  });
});
