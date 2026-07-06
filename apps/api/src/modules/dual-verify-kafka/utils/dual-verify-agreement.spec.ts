import { describe, expect, it } from 'vitest';
import { compareDualVerifyResults } from './dual-verify-agreement';

const SAMPLE_LANDING = `2.1 Senior Management
Senior management must approve the SCP annually.

Reference PDF :
I M P T F S.pdf

Output/Response :
Page 6, Section 2.1: 'Senior management approval documented annually.'

Fulfilled clauses :
• Annual approval — covered

Comply Yes/No (Status) : Compliant
Compliance Confidence % : 90%
Corrective Action Plan : N/A
Responsibility : N/A`;

const SAMPLE_LLM_ALIGNED = `2.1 Senior Management
Senior management must approve the SCP annually.

Reference PDF :
I M P T F S.pdf

Output/Response :
Page 6, Section 2.1: 'Annual senior management sign-off.'

Fulfilled clauses :
• Annual approval — covered

Comply Yes/No (Status) : Compliant
Compliance Confidence % : 88%
Corrective Action Plan : N/A
Responsibility : N/A`;

const SAMPLE_LLM_MISMATCH = `2.1 Senior Management
Senior management must approve the SCP annually.

Reference PDF :
I M P T F S.pdf

Output/Response :
No corresponding procedure found.

Fulfilled clauses :
None

Comply Yes/No (Status) : Non-Compliant
Compliance Confidence % : 20%
Corrective Action Plan : Gap(s): (1) Missing annual approval evidence
Responsibility : Compliance Team`;

describe('compareDualVerifyResults', () => {
  it('returns aligned when status and confidence match closely', () => {
    const result = compareDualVerifyResults(SAMPLE_LANDING, SAMPLE_LLM_ALIGNED);
    expect(result.status).toBe('aligned');
  });

  it('returns status_mismatch when Phase 2 disagrees', () => {
    const result = compareDualVerifyResults(SAMPLE_LANDING, SAMPLE_LLM_MISMATCH);
    expect(result.status).toBe('status_mismatch');
    expect(result.landingStatus).toBe('Compliant');
    expect(result.llmStatus).toBe('Non-Compliant');
  });
});
