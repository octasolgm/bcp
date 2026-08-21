import {
  chapterKeyFromSectionRef,
  compareInternalSectionRef,
  groupInternalSectionsForDisplay,
  normalizeInternalSectionRef,
  sortInternalSectionsByPointRef,
} from './internal-section-group';
import { comparePointNumber } from './list-utils';
import type { InternalDocumentSection } from './types';

function section(ref: string, id = ref): InternalDocumentSection {
  return {
    id,
    sectionRef: ref,
    sectionText: `Text for ${ref}`,
    sourcePage: 1,
  };
}

describe('normalizeInternalSectionRef', () => {
  it('cleans legacy dedupe ids', () => {
    expect(normalizeInternalSectionRef('1.-1')).toBe('1.1');
    expect(normalizeInternalSectionRef('1.-2')).toBe('1.2');
    expect(normalizeInternalSectionRef('9.4.1')).toBe('9.4.1');
  });
});

describe('sortInternalSectionsByPointRef', () => {
  it('orders refs numerically point-wise', () => {
    const sorted = sortInternalSectionsByPointRef([
      section('14.4', 'd'),
      section('1.10', 'c'),
      section('9.4.1', 'e'),
      section('1.2', 'b'),
      section('1.1', 'a'),
      section('6.18-b', 'g'),
      section('6.18-a', 'f'),
    ]).map((s) => s.sectionRef);

    expect(sorted).toEqual(['1.1', '1.2', '1.10', '6.18-a', '6.18-b', '9.4.1', '14.4']);
  });

  it('puts parent headings before numbered children', () => {
    const sorted = sortInternalSectionsByPointRef([
      section('7.1', 'a'),
      section('7', 'b'),
      section('7.7-b', 'c'),
      section('7.7', 'd'),
      section('7.7-a', 'e'),
      section('6.15', 'f'),
      section('6', 'g'),
      section('8', 'h'),
      section('10', 'i'),
    ]).map((s) => s.sectionRef);

    expect(sorted).toEqual(['6', '6.15', '7', '7.1', '7.7', '7.7-a', '7.7-b', '8', '10']);
  });

  it('normalizes legacy 1.-2 before sorting', () => {
    const sorted = sortInternalSectionsByPointRef([
      section('1.-2', 'b'),
      section('1.1', 'a'),
    ]).map((s) => normalizeInternalSectionRef(s.sectionRef));

    expect(sorted).toEqual(['1.1', '1.2']);
  });
});

describe('compareInternalSectionRef', () => {
  it('uses outline ascending order', () => {
    expect(compareInternalSectionRef('7', '7.1')).toBeLessThan(0);
    expect(compareInternalSectionRef('7.7', '7.7-a')).toBeLessThan(0);
    expect(compareInternalSectionRef('9', '10')).toBeLessThan(0);
    expect(compareInternalSectionRef('1.2', '1.10')).toBeLessThan(0);
  });
});

describe('comparePointNumber', () => {
  it('matches API point ordering', () => {
    expect(comparePointNumber('1.2', '1.10', 'asc')).toBeLessThan(0);
    expect(comparePointNumber('9.4.1', '14.4', 'asc')).toBeLessThan(0);
    expect(comparePointNumber('6.18-a', '6.18-b', 'asc')).toBeLessThan(0);
  });
});

describe('groupInternalSectionsForDisplay', () => {
  it('groups by top-level chapter without nested regulation picker buckets', () => {
    const groups = groupInternalSectionsForDisplay([
      section('1.-1', 'a'),
      section('1.-2', 'b'),
      section('9.4.1', 'c'),
    ]);

    expect(groups.length).toBe(2);
    expect(groups[0].chapter).toBe('1');
    expect(groups[0].sections.length).toBe(2);
    expect(groups[1].chapter).toBe('9');
    expect(groups[1].sections.length).toBe(1);
    expect(chapterKeyFromSectionRef('1.-1')).toBe('1');
  });
});
