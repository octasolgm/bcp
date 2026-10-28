import type { InternalDocumentSection } from './types';

export type InternalSectionChapterGroup = {
  chapter: string;
  label: string;
  sections: InternalDocumentSection[];
};

/** Display cleanup for legacy dedupe ids like `1.-1` → `1.1`. */
export function normalizeInternalSectionRef(ref: string): string {
  const trimmed = (ref ?? '').trim();
  if (!trimmed) return trimmed;
  return trimmed
    .replace(/^(\d+)\.+-(\d+)$/, '$1.$2')
    .replace(/\.+-/g, '.');
}

/** Outline order: 1, 6, 6.1, 7, 7.1, 7.7, 7.7-a, 7.7-b, 8 (parent before children). */
export function compareInternalSectionRef(a: string, b: string): number {
  const partsA = parseInternalSectionTokens(a);
  const partsB = parseInternalSectionTokens(b);
  const len = Math.max(partsA.length, partsB.length);
  for (let i = 0; i < len; i++) {
    if (i >= partsA.length) return -1;
    if (i >= partsB.length) return 1;
    const ta = partsA[i];
    const tb = partsB[i];
    if (ta.num !== tb.num) return ta.num - tb.num;
    const suffixCmp = ta.suffix.localeCompare(tb.suffix);
    if (suffixCmp !== 0) return suffixCmp;
  }
  return 0;
}

function parseInternalSectionTokens(raw: string): { num: number; suffix: string }[] {
  const head = (normalizeInternalSectionRef(raw).split(/\s+/)[0] ?? '').replace(/\.$/, '');
  if (!head) return [];
  return head
    .split(/[.-]/)
    .filter(Boolean)
    .map((segment) => {
      const m = segment.match(/^(\d+)([a-z]*)$/i);
      if (m) {
        return { num: Number.parseInt(m[1], 10), suffix: (m[2] ?? '').toLowerCase() };
      }
      if (/^[a-z]+$/i.test(segment)) {
        return { num: -1, suffix: segment.toLowerCase() };
      }
      const digits = segment.replace(/\D/g, '');
      return digits
        ? { num: Number.parseInt(digits, 10), suffix: '' }
        : { num: -1, suffix: segment.toLowerCase() };
    });
}

/** Sort internal sections ascending by number: 1, 1.2, 1.10, 7, 7.7-a, 8. */
export function sortInternalSectionsByPointRef(
  sections: InternalDocumentSection[],
): InternalDocumentSection[] {
  return [...sections].sort((a, b) => {
    const refCmp = compareInternalSectionRef(a.sectionRef, b.sectionRef);
    if (refCmp !== 0) return refCmp;

    const orderA = a.displayOrder ?? Number.MAX_SAFE_INTEGER;
    const orderB = b.displayOrder ?? Number.MAX_SAFE_INTEGER;
    if (orderA !== orderB) return orderA - orderB;

    const pageA = a.sourcePage ?? Number.MAX_SAFE_INTEGER;
    const pageB = b.sourcePage ?? Number.MAX_SAFE_INTEGER;
    return pageA - pageB;
  });
}

export function chapterKeyFromSectionRef(ref: string): string {
  const normalized = normalizeInternalSectionRef(ref);
  const m = normalized.match(/^(\d+)/);
  return m?.[1] ?? 'other';
}

export function formatInternalChapterLabel(chapter: string): string {
  if (chapter === 'other') return 'Other sections';
  if (chapter === 'intro') return 'Introduction';
  return `§${chapter}`;
}

/** Flat chapter → sections grouping for internal policy manuals (not regulation picker rules). */
export function groupInternalSectionsForDisplay(
  sections: InternalDocumentSection[],
): InternalSectionChapterGroup[] {
  const sorted = sortInternalSectionsByPointRef(sections);
  const byChapter = new Map<string, InternalDocumentSection[]>();

  for (const section of sorted) {
    const chapter = chapterKeyFromSectionRef(section.sectionRef);
    const list = byChapter.get(chapter) ?? [];
    list.push(section);
    byChapter.set(chapter, list);
  }

  return [...byChapter.entries()]
    .sort(([a], [b]) => {
      if (a === 'other') return 1;
      if (b === 'other') return -1;
      return compareInternalSectionRef(a, b);
    })
    .map(([chapter, chapterSections]) => ({
      chapter,
      label: formatInternalChapterLabel(chapter),
      sections: chapterSections,
    }));
}
