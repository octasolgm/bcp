import {
  buildPolicyRefProofs,
  formatPolicyRefLabel,
  parsePolicyCitationFromLine,
  parseRegulDocumentReferenceLines,
  sanitizePolicySection,
} from './policy-doc-resolve';

describe('sanitizePolicySection', () => {
  it('strips trailing punctuation junk', () => {
    expect(sanitizePolicySection('2).')).toBe('2');
    expect(sanitizePolicySection('7.2):')).toBe('7.2');
    expect(sanitizePolicySection('Legal Basis).')).toBe('Legal Basis');
  });

  it('drops regulation point UUID leaks', () => {
    expect(
      sanitizePolicySection(
        '31e325e2-5e6f-4df1-859b-203afe942c0c International Legislative and Regulatory Framework)',
      ),
    ).toBeNull();
  });

  it('keeps short clause numbers when title is appended', () => {
    expect(sanitizePolicySection('7.2 Identification of Suspicious Transactions')).toBe('7.2');
  });
});

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

  it('cleans UUID section noise from page-first cites', () => {
    const parsed = parsePolicyCitationFromLine(
      'Page 6, Section 31e325e2-5e6f-4df1-859b-203afe942c0c International Legislative and Regulatory Framework): "The UAE is one of the founding members of MENA"',
    );
    expect(parsed.page).toBe('6');
    expect(parsed.section).toBeNull();
    expect(parsed.quote).toContain('founding members');
    expect(
      formatPolicyRefLabel({
        page: parsed.page ?? '',
        section: parsed.section,
        docId: null,
        docLabel: 'Policy',
        quote: parsed.quote,
      }),
    ).toBe('Page 6');
  });

  it('cleans Section 2). junk', () => {
    const parsed = parsePolicyCitationFromLine(
      'Page 2, Section 2).: "This document also considers standards"',
    );
    expect(parsed.page).toBe('2');
    expect(parsed.section).toBe('2');
  });
});

describe('parseRegulDocumentReferenceLines', () => {
  const catalog = [{ id: 'doc-1', originalFileName: 'Internal AML Manual 290626 (1).pdf' }];

  it('parses Regul document_reference lines (semicolon or newline separated)', () => {
    const refs = parseRegulDocumentReferenceLines(
      'Internal AML Manual — section 6.2, p.12; Internal AML Manual — section 9.4.1, p.63',
      catalog,
    );
    expect(refs.length).toBe(2);
    expect(refs[0].section).toBe('6.2');
    expect(refs[0].page).toBe('12');
    expect(refs[1].section).toBe('9.4.1');
    expect(refs[1].page).toBe('63');
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

  it('dedupes same quote repeated with UUID section junk', () => {
    const landing = `Reference PDF : Manual.pdf

Output/Response :
Page 6, Section 31e325e2-5e6f-4df1-859b-203afe942c0c International Legislative): "The UAE is one of the founding members of MENA"
Page 6, Section 2: "The UAE is one of the founding members of MENA"

Comply Yes/No (Status) : Compliant
`;
    const refs = buildPolicyRefProofs(landing, '', [{ id: 'd1', originalFileName: 'Manual.pdf' }]);
    const sameQuote = refs.filter((r) => (r.quote ?? '').includes('founding members'));
    expect(sameQuote.length).toBe(1);
  });
});
