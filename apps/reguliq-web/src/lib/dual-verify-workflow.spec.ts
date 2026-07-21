import {
  complianceFilterLabel,
  complianceKeyFromBreakdownName,
  DUAL_VERIFY_COPY,
  getRunBlockedReason,
  parseComplianceFilter,
} from './dual-verify-workflow';

describe('dual-verify-workflow', () => {
  it('blocks run when internal policy PDF is missing', () => {
    expect(
      getRunBlockedReason({ persistenceOk: true, hasInternalFile: false, selectedCount: 3 }),
    ).toBe(DUAL_VERIFY_COPY.internalPolicyMissing);
  });

  it('blocks run when no gov points selected', () => {
    expect(
      getRunBlockedReason({ persistenceOk: true, hasInternalFile: true, selectedCount: 0 }),
    ).toBe(DUAL_VERIFY_COPY.govPointsMissing);
  });

  it('allows run when prerequisites are met', () => {
    expect(
      getRunBlockedReason({ persistenceOk: true, hasInternalFile: true, selectedCount: 2 }),
    ).toBeNull();
  });

  it('parses compliance query params', () => {
    expect(parseComplianceFilter('non-compliant')).toBe('non-compliant');
    expect(parseComplianceFilter('partial')).toBe('partial');
    expect(parseComplianceFilter('')).toBeNull();
  });

  it('maps dashboard breakdown names to compliance keys', () => {
    expect(complianceKeyFromBreakdownName('Non-compliant')).toBe('non-compliant');
    expect(complianceKeyFromBreakdownName('Partial')).toBe('partial');
    expect(complianceKeyFromBreakdownName('Compliant')).toBe('compliant');
  });

  it('labels compliance filters for display', () => {
    expect(complianceFilterLabel('partial')).toBe('Partial');
  });
});
