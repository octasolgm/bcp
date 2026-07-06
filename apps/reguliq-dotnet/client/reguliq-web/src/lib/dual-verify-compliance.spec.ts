import { filterReportByCompliance, pass2ComplianceBucket } from './dual-verify-compliance';
import type { DualVerifyReportItem } from './dual-verify-report';

function item(llmStatus: string): DualVerifyReportItem {
  return {
    pointId: '2.1.1',
    status: 'completed',
    landingMessage: 'x',
    llmMessage: 'y',
    agreement: {
      status: 'aligned',
      label: 'Aligned',
      landingStatus: 'Compliant',
      llmStatus,
      landingConfidence: 90,
      llmConfidence: 88,
      confidenceDelta: 2,
      summary: 'ok',
    },
  };
}

describe('dual-verify-compliance', () => {
  it('classifies pass-2 compliance buckets', () => {
    expect(pass2ComplianceBucket(item('Compliant'))).toBe('compliant');
    expect(pass2ComplianceBucket(item('Partial Compliant'))).toBe('partial');
    expect(pass2ComplianceBucket(item('Non-Compliant'))).toBe('non-compliant');
  });

  it('filters report items by compliance status', () => {
    const rows = [item('Compliant'), item('Non-Compliant'), item('Partial Compliant')];
    expect(filterReportByCompliance(rows, 'partial')).toEqual([rows[2]]);
    expect(filterReportByCompliance(rows, null)).toEqual(rows);
  });
});
