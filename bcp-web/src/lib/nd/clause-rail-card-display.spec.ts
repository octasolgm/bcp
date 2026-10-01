import { describe, expect, it } from 'vitest';
import {
  buildClauseRailCardFields,
  railClauseExcerpt,
  resolveClauseRailHeading,
} from './clause-rail-card-display';

describe('resolveClauseRailHeading', () => {
  it('returns empty when title is only the clause number', () => {
    expect(resolveClauseRailHeading('3.3', '3.3')).toBe('');
    expect(resolveClauseRailHeading('3.3', '§3.3')).toBe('');
  });

  it('strips a leading clause prefix from combined titles', () => {
    expect(resolveClauseRailHeading('3.3', '3.3 Protection against Liability')).toBe(
      'Protection against Liability',
    );
    expect(resolveClauseRailHeading('2.1', '§2.1 — Introduction')).toBe('Introduction');
  });

  it('keeps titles that do not repeat the clause number', () => {
    expect(resolveClauseRailHeading('3.3', 'Protection against Liability')).toBe(
      'Protection against Liability',
    );
  });
});

describe('railClauseExcerpt', () => {
  it('removes clause number and heading from the body excerpt', () => {
    const raw =
      '3.3 Protection against Liability The AML-CFT Law and the AML-CFT Decision require…';
    expect(railClauseExcerpt('3.3', 'Protection against Liability', raw)).toBe(
      'The AML-CFT Law and the AML-CFT Decision require…',
    );
  });
});

describe('buildClauseRailCardFields', () => {
  it('aligns number, heading, and excerpt for combined titles', () => {
    const card = buildClauseRailCardFields({
      clauseNum: '§2.1',
      title: '2.1 — Introduction',
      clauseText: '2.1 Introduction This policy sets out…',
    });
    expect(card.clauseNum).toBe('2.1');
    expect(card.heading).toBe('Introduction');
    expect(card.clauseText).toBe('This policy sets out…');
  });

  it('infers heading from body when stored title is only the clause number', () => {
    const card = buildClauseRailCardFields({
      clauseNum: '3.3',
      title: '3.3',
      clauseText:
        '3.3 Protection against Liability for Reporting Persons (AML-CFT Law Article 27) The AML-CFT Law…',
    });
    expect(card.heading).toBe('Protection against Liability for Reporting Persons');
    expect(card.clauseText).toContain('AML-CFT Law');
  });
});
