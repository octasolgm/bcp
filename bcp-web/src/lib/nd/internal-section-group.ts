import type { InternalDocumentSection } from './types';
import { comparePointNumber } from './list-utils';

export type InternalSectionChapterGroup = {
  chapter: string;
  label: string;
  sections: InternalDocumentSection[];
};

/** Display cleanup for legacy dedupe ids like `1.-1` → `1.1`. */
export function normalizeInternalSectionRef(ref: string): string {
  const trimmed = ref.trim();
  if (!trimmed) return trimmed;
  return trimmed
    .replace(/^(\d+)\.+-(\d+)$/, '$1.$2')
    .replace(/\.+-/g, '.');
}

function sortKey(ref: string): string {
  return normalizeInternalSectionRef(ref);
}

/** Sort internal sections point-wise: 1.1, 1.2, 1.10, 9.4.1, 14.4 (not string order). */
export function sortInternalSectionsByPointRef(
  sections: InternalDocumentSection[],
): InternalDocumentSection[] {
  return [...sections].sort((a, b) => {
    const refCmp = comparePointNumber(
      sortKey(a.sectionRef),
      sortKey(b.sectionRef),
      'asc',
    );
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
      return comparePointNumber(a, b, 'asc');
    })
    .map(([chapter, chapterSections]) => ({
      chapter,
      label: formatInternalChapterLabel(chapter),
      sections: chapterSections,
    }));
}
