import {
  assignNestedIdsToDuplicateSiblings,
  expandGovPointSubLeaves,
  extractNumericClauseRef,
  filterComparableGovLeafPoints,
  formatSectionGroupLabel,
  isJunkExtractPointId,
  normalizeNumericPointId,
  resolveGovPointDisplayNumber,
  sectionHeadingTitleForKey,
  synthesizeMissingParentGovPoints,
} from './gov-point-filter';
import { assignUniqueLibraryPointIds } from './library-points-utils';

describe('gov-point-filter', () => {
  it('normalizes numeric point ids', () => {
    expect(normalizeNumericPointId('2.1.1.')).toBe('2.1.1');
    expect(normalizeNumericPointId('2')).toBeNull();
  });

  it('nests duplicate sibling numbers for display (7.8 → 7.8.1 …)', () => {
    const points = [
      { point_id: '7.8', title: 'Establish policies', text: 'Policy text A', section: '7.8 Confidentiality' },
      { point_id: '7.8', title: 'Confidentiality when reporting', text: 'Policy text B', section: '7.8 Confidentiality' },
      { point_id: '7.8', title: 'Prohibition against Tipping Off', text: 'Policy text D', section: '7.8 Confidentiality' },
    ];
    const nested = assignNestedIdsToDuplicateSiblings(points);
    expect(nested.map((p) => p.point_id)).toEqual(['7.8.1', '7.8.2', '7.8.3']);
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
    expect(
      resolveGovPointDisplayNumber({
        point_id: '3.1.1',
        pointNumber: '3.1',
        title: 'Identify, assess, understand risks',
        text: 'LFIs must identify…',
        section: '3.1. Summary of Minimum Statutory Obligations',
      }),
    ).toBe('3.1.1');
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

  it('skips Part I/II junk ids and shows numeric clause from section', () => {
    expect(isJunkExtractPointId('Part III')).toBe(true);
    expect(extractNumericClauseRef('2.6.6 Customer Due Diligence')).toBe('2.6.6');
    expect(
      resolveGovPointDisplayNumber({
        point_id: 'd427cea1-27fb-47e9-8b29-f4fe116fa1df',
        pointNumber: 'Part III',
        title: 'Customer Due Diligence',
        text: 'LFIs must conduct CDD…',
        section: '2.6.6 Customer Due Diligence · p. 42',
      }),
    ).toBe('2.6.6');

    const { comparable, skipped } = filterComparableGovLeafPoints([
      {
        point_id: 'uuid-1',
        pointNumber: 'Part III',
        title: 'Customer Due Diligence',
        text: 'LFIs must conduct CDD on all customers.',
        section: '2.6.6 Customer Due Diligence',
      },
    ]);
    expect(comparable).toHaveLength(1);
    expect(skipped).toHaveLength(0);
  });

  it('keeps expanded leaf clause ids as selection keys (not parent UUID)', () => {
    const parentUuid = '1c6c2e26-7efa-4018-bd65-5fa71b4f0125';
    const expanded = expandGovPointSubLeaves([
      {
        point_id: '3.1',
        title: 'Management systems',
        text: 'Obligations include: * Implement suspicious transaction indicators; * Maintain records.',
        section: '3.1 Management systems',
      },
    ]);
    const withIds = assignUniqueLibraryPointIds(
      expanded.map((p) => ({
        ...p,
        regulationPointId: parentUuid,
        libraryId: 'lib',
        docId: 'doc',
        docName: 'CBUAE',
      })),
    );
    expect(withIds.map((p) => p.point_id)).toEqual(['3.1.1', '3.1.2']);
    expect(withIds[0].pointNumber).toBe('3.1.1');
    expect(withIds[0].regulationPointId).toBe(parentUuid);
    expect(withIds[0].text).toContain('suspicious transaction');
  });

  it('resolves section heading title for §3.1 groups', () => {
    const points = [
      {
        point_id: '3.1',
        title: 'Summary of Minimum Statutory Obligations of Supervised Institutions',
        text: 'Long section body…',
        section: '3.1. Summary of Minimum Statutory Obligations of Supervised Institutions',
      },
      {
        point_id: '3.1.1',
        title: 'Identify, assess, understand risks',
        text: 'LFIs must identify…',
        section: '3.1. Summary of Minimum Statutory Obligations of Supervised Institutions',
      },
    ];
    expect(sectionHeadingTitleForKey('3.1', points)).toBe(
      'Summary of Minimum Statutory Obligations of Supervised Institutions',
    );
    expect(formatSectionGroupLabel('3.1', sectionHeadingTitleForKey('3.1', points))).toBe(
      '§3.1 Summary of Minimum Statutory Obligations of Supervised Institutions',
    );
  });

  it('resolves §3.1 heading from child points when parent row is absent', () => {
    const points = [
      {
        point_id: '3.1.1',
        title: 'Identify, assess, understand risks',
        text: 'LFIs must identify…',
        section: '3.1. Summary of Minimum Statutory Obligations of Supervised Institutions · p. 12',
      },
      {
        point_id: '3.1.2',
        title: 'Define scope and take due diligence measures',
        text: 'LFIs must define…',
        section: 'p. 12',
      },
    ];
    expect(sectionHeadingTitleForKey('3.1', points)).toBe(
      'Summary of Minimum Statutory Obligations of Supervised Institutions',
    );
  });

  it('resolves §3.1 heading from parent body when title field is empty', () => {
    const points = [
      {
        point_id: '3.1',
        title: '',
        text: 'Summary of Minimum Statutory Obligations of Supervised Institutions\n\n• Identify risks',
        section: 'p. 12',
      },
      {
        point_id: '3.1.1',
        title: 'Identify, assess, understand risks',
        text: 'LFIs must identify…',
        section: 'p. 12',
      },
    ];
    expect(sectionHeadingTitleForKey('3.1', points)).toBe(
      'Summary of Minimum Statutory Obligations of Supervised Institutions',
    );
  });

  it('synthesizes missing parent section rows', () => {
    const points = [
      {
        point_id: '3.1.1',
        title: 'Identify risks',
        text: 'LFIs must identify…',
        section: '3.1. Summary of Minimum Statutory Obligations · p. 12',
      },
      {
        point_id: '3.1.2',
        title: 'Define scope',
        text: 'LFIs must define…',
        section: 'p. 12',
      },
    ];
    const catalog = synthesizeMissingParentGovPoints(points);
    expect(sectionHeadingTitleForKey('3.1', catalog)).toBe(
      'Summary of Minimum Statutory Obligations',
    );
  });
});
