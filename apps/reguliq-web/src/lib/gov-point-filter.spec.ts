import {
  expandGovPointSubLeaves,
  filterComparableGovLeafPoints,
  normalizeNumericPointId,
} from './gov-point-filter';

describe('gov-point-filter', () => {
  it('normalizes numeric point ids', () => {
    expect(normalizeNumericPointId('2.1.1.')).toBe('2.1.1');
    expect(normalizeNumericPointId('2')).toBeNull();
  });

  it('expands §3.1 bullet list into 3.1.1, 3.1.2, …', () => {
    const points = [
      {
        point_id: '3.1',
        title: 'Prohibition of Sanctions Evasion Activities',
        text: 'LFIs must not engage in activities including but not limited to: * Tipping off customers; * Omitting information; * Providing false information.',
        section: '3.1. Sanctions Evasion',
      },
    ];
    const expanded = expandGovPointSubLeaves(points);
    expect(expanded.map((p) => p.point_id)).toEqual(['3.1.1', '3.1.2', '3.1.3']);
    const { comparable } = filterComparableGovLeafPoints(points);
    expect(comparable.map((p) => p.point_id)).toEqual(['3.1.1', '3.1.2', '3.1.3']);
  });

  it('expands customer screening colon labels into 3.3.1 …', () => {
    const points = [
      {
        point_id: '3.3',
        title: 'Customer Screening',
        text: 'Screening processes should be conducted at various stages of the customer lifecycle to include: Periodic name screening: A change triggers rescreening. Ad hoc name screening: Such screening is triggered by a business need. Re-screening: A specific scenario identifies high-risk jurisdiction.',
        section: '3.3. Customer Screening',
      },
    ];
    const { comparable } = filterComparableGovLeafPoints(points);
    expect(comparable.map((p) => p.point_id)).toEqual(['3.3.1', '3.3.2', '3.3.3']);
  });
});
