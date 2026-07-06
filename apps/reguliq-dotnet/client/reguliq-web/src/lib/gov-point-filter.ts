export type GovPoint = {
  point_id: string;
  title?: string;
  text: string;
  section?: string;
  page_hint?: number;
  point_type?: 'mandatory' | 'informational' | 'definition';
};

const OBLIGATION_PATTERN =
  /\b(must|shall|should|required to|obliged to|ensure that|are required|need to|have to|lfis should|lfi should)\b/i;

const INFO_TITLE_PATTERNS = [
  /^introduction$/i,
  /^introductory\b/i,
  /^foreword$/i,
  /^preface$/i,
  /^table of contents$/i,
  /^contents$/i,
  /^document history$/i,
  /^version history$/i,
  /^revision history$/i,
  /^acknowledg/i,
  /^disclaimer$/i,
  /^about this (document|guidance)$/i,
  /^overview$/i,
  /^background$/i,
  /^applicability$/i,
  /^scope$/i,
];

const SCOPE_TEXT_PATTERN =
  /^(unless otherwise noted,\s*)?this guidance applies to\b/i;

/** Keep in sync with apps/api/src/modules/landing-ai/utils/gov-point-filter.ts */
export function normalizeNumericPointId(pointId: string): string | null {
  const id = pointId.trim();
  if (!/^\d+(?:\.\d+)*\.?$/.test(id)) return null;
  if (!id.includes('.') && !id.endsWith('.')) return null;
  return id.replace(/\.$/, '');
}

export function isNumericSectionParentId(pointId: string): boolean {
  const id = pointId.trim();
  return /^\d+\.$/.test(id) || /^\d+\.\d+(?:\.\d+)*\.?$/.test(id);
}

export function isNumericParentWithChildren(
  pointId: string,
  allPointIds: string[],
): boolean {
  if (!isNumericSectionParentId(pointId)) return false;
  const norm = normalizeNumericPointId(pointId);
  if (!norm) return false;
  const prefix = `${norm}.`;
  return allPointIds.some((other) => {
    if (other.trim() === pointId.trim()) return false;
    const otherNorm = normalizeNumericPointId(other);
    return otherNorm !== null && otherNorm.startsWith(prefix);
  });
}

export function isNamedSectionSummaryPoint(pointId: string): boolean {
  const id = pointId.trim();
  return /^[A-Za-z]/.test(id) && id.includes(' - ') && !/^Article\b/i.test(id);
}

/** Extract artifact: bare "2" / "3" labels in definition lists — not section ids like 2.1. */
export function isBareDefinitionLabel(pointId: string): boolean {
  return /^\d+$/.test(pointId.trim());
}

export function numericSubPointSectionKey(pointId: string): string | null {
  const norm = normalizeNumericPointId(pointId);
  if (!norm) return null;
  const parts = norm.split('.');
  if (parts.length >= 3) return `${parts[0]}.${parts[1]}`;
  return null;
}

export function numericSectionHeaderKey(
  pointId: string,
  allPointIds: string[],
): string | null {
  const norm = normalizeNumericPointId(pointId);
  if (!norm) return null;
  const parts = norm.split('.');
  if (parts.length !== 2) return null;
  if (!isNumericParentWithChildren(pointId, allPointIds)) return null;
  return norm;
}

/** §1 and all subpoints — compare starts at §2. Keep in sync with API gov-point-filter.ts */
export function isSectionOnePoint(pointId: string, section: string): boolean {
  const id = pointId.trim();
  if (/^1(\.|$)/.test(id) || /^1\.\d/.test(id)) return true;
  const topLevel = id.match(/^(\d+)/);
  if (topLevel && topLevel[1] === '1') return true;
  if (/^1(\.|\s)/.test(section.trim())) return true;
  return false;
}

const ANNEX_SUBSECTION_HEADING_RE =
  /^\d+\.\s+(?:Red Flag Indicators|Lessons learned)/i;

/**
 * Annexes and red-flag indicator lists — not main guidance chapters (§2–§4).
 * Keep in sync with apps/api/src/modules/landing-ai/utils/gov-point-filter.ts
 */
export function isAnnexPoint(point: {
  point_id: string;
  title?: string;
  section?: string;
}): boolean {
  const pointId = point.point_id.trim();
  const section = (point.section ?? '').trim();
  const title = (point.title ?? '').trim();

  if (/^annexes?\b/i.test(section)) return true;
  if (/^annex\s+\d+/i.test(section) || /\bannex\s+\d+\s*·/i.test(section)) {
    return true;
  }
  if (/^annexes?\s*-/i.test(pointId)) return true;
  if (ANNEX_SUBSECTION_HEADING_RE.test(section)) return true;
  if (/red flag indicators for (tf|pf)\b/i.test(section)) return true;
  if (/^red flag indicators for (tf|pf)\b/i.test(title)) return true;
  if (/FATF Typologies Report on Proliferation Financing/i.test(title)) {
    return true;
  }
  if (
    /^\([ivxlcdm]+\)$/i.test(pointId) &&
    (ANNEX_SUBSECTION_HEADING_RE.test(section) || /red flag/i.test(section))
  ) {
    return true;
  }
  if (pointId === '1' && /annex\s+1/i.test(section) && /red flag/i.test(title)) {
    return true;
  }

  return false;
}

export type GovPointClassification = {
  comparable: boolean;
  reason?: string;
};

/** Keep in sync with apps/api/src/modules/landing-ai/utils/gov-point-filter.ts */
export function classifyGovPoint(point: GovPoint): GovPointClassification {
  const pointId = point.point_id.trim();
  const section = (point.section ?? '').trim();

  if (isSectionOnePoint(pointId, section)) {
    return {
      comparable: false,
      reason: '§1 and subpoints skipped (compare starts at §2)',
    };
  }

  if (isAnnexPoint(point)) {
    return {
      comparable: false,
      reason: 'annex / red-flag indicators skipped (main body §2–§4 only)',
    };
  }

  if (point.point_type === 'informational') {
    return { comparable: false, reason: 'informational (extract tag)' };
  }
  if (point.point_type === 'mandatory') {
    return { comparable: true };
  }

  const title = (point.title ?? '').trim();
  const text = point.text.trim();

  if (/^purpose$/i.test(title) && /^the purpose of this/i.test(text)) {
    if (!OBLIGATION_PATTERN.test(text)) {
      return { comparable: false, reason: 'document purpose (informational)' };
    }
  }

  if (pointId.toLowerCase().includes('purpose of this guidance - purpose')) {
    return { comparable: false, reason: 'document purpose (informational)' };
  }

  if (pointId.toLowerCase().includes('purpose of this guidance - applicability')) {
    return { comparable: false, reason: 'document applicability (informational)' };
  }

  if (INFO_TITLE_PATTERNS.some((p) => p.test(title))) {
    return { comparable: false, reason: 'introduction or informational heading' };
  }

  if (/^introduction\b/i.test(section) && !OBLIGATION_PATTERN.test(text)) {
    return { comparable: false, reason: 'introduction section' };
  }

  if (
    !OBLIGATION_PATTERN.test(text) &&
    (SCOPE_TEXT_PATTERN.test(text) ||
      (/^applicability$/i.test(title) && !OBLIGATION_PATTERN.test(text)))
  ) {
    return { comparable: false, reason: 'applicability / scope (informational)' };
  }

  if (
    text.length < 400 &&
    /\b(means|refers to|is defined as|is a technique|is an algorithm)\b/i.test(
      text,
    ) &&
    !OBLIGATION_PATTERN.test(text)
  ) {
    return { comparable: false, reason: 'definition only (no obligation)' };
  }

  if (text.length > 80 && !OBLIGATION_PATTERN.test(text)) {
    const looksInformational =
      /^the purpose of/i.test(text) ||
      /^this document (describes|provides|sets out)/i.test(text);
    if (looksInformational) {
      return { comparable: false, reason: 'informational narrative' };
    }
  }

  return { comparable: true };
}

export function cleanLegacyPromptFromRequirementText(text: string): string {
  return text
    .replace(/^Compare this entire government section[^\n]*\n\n/i, '')
    .replace(/^All sub-requirements below must be evaluated together:\s*\n\n/i, '')
    .replace(/^####\s+/gm, '')
    .trim();
}

export function formatGovRequirementForDisplay(point: GovPoint): string {
  return cleanLegacyPromptFromRequirementText(point.text.trim());
}

function sectionTitleFromPoints(key: string, group: GovPoint[]): string {
  for (const p of group) {
    const section = (p.section ?? '').trim();
    const m = section.match(new RegExp(`^${key.replace('.', '\\.')}\\.\\s*(.+)$`, 'i'));
    if (m?.[1]) return m[1].trim();
  }
  const titled = group.find((p) => p.title?.trim());
  return titled?.title?.trim() ?? key;
}

function mergeSectionGroup(key: string, group: GovPoint[]): GovPoint {
  const sorted = [...group].sort((a, b) =>
    a.point_id.localeCompare(b.point_id, undefined, { numeric: true }),
  );
  const title = sectionTitleFromPoints(key, sorted);
  const section =
    sorted.find((p) => (p.section ?? '').trim().startsWith(`${key}.`))?.section ??
    sorted[0].section ??
    key;
  const text = sorted
    .map((p) => {
      const label = [p.point_id, p.title].filter(Boolean).join(' — ');
      return `${label}\n${p.text.trim()}`;
    })
    .join('\n\n');
  const pageHint = sorted.reduce(
    (min, p) =>
      p.page_hint !== undefined && (min === undefined || p.page_hint < min)
        ? p.page_hint
        : min,
    undefined as number | undefined,
  );

  return {
    point_id: key,
    title,
    text: sorted
      .map((p) => {
        const head = [p.point_id, p.title].filter(Boolean).join(' — ');
        return `#### ${head}\n${p.text.trim()}`;
      })
      .join('\n\n'),
    section,
    page_hint: pageHint,
  };
}

export function rollupGovPointsToSections(
  points: GovPoint[],
  allPointIds: string[],
): {
  comparable: GovPoint[];
  skipped: Array<{ point: GovPoint; reason: string }>;
} {
  const groups = new Map<string, GovPoint[]>();
  const standalone: GovPoint[] = [];
  const skipped: Array<{ point: GovPoint; reason: string }> = [];

  for (const point of points) {
    const subKey = numericSubPointSectionKey(point.point_id);
    const headerKey = numericSectionHeaderKey(point.point_id, allPointIds);
    const headingKey = sectionRollupKeyFromHeading(point);
    const groupKey = subKey ?? headerKey ?? headingKey;

    if (groupKey) {
      const list = groups.get(groupKey) ?? [];
      list.push(point);
      groups.set(groupKey, list);
      if (subKey || headingKey) {
        skipped.push({
          point,
          reason: headingKey
            ? `rolled up into section ${groupKey} (section heading compare)`
            : `rolled up into section ${groupKey} (whole-section compare)`,
        });
      }
    } else {
      standalone.push(point);
    }
  }

  const rolled: GovPoint[] = [];
  for (const [key, group] of groups) {
    rolled.push(mergeSectionGroup(key, group));
  }

  const comparable = [...rolled, ...standalone].sort((a, b) =>
    compareGovPointIds(a.point_id, b.point_id),
  );

  return { comparable, skipped };
}

/** Numeric sections (2.1, 2.3) before roman (i), articles, and named ids. */
export function compareGovPointIds(a: string, b: string): number {
  const rank = (id: string): number => {
    const t = id.trim();
    if (/^\d+(?:\.\d+)*\.?$/.test(t)) return 0;
    if (/^\([ivxlcdm]+\)$/i.test(t)) return 1;
    if (/^Article\b/i.test(t)) return 2;
    return 3;
  };
  const ra = rank(a);
  const rb = rank(b);
  if (ra !== rb) return ra - rb;
  return a.localeCompare(b, undefined, { numeric: true, sensitivity: 'base' });
}

export function filterComparableGovPoints(points: GovPoint[]): {
  comparable: GovPoint[];
  skipped: Array<{ point: GovPoint; reason: string }>;
} {
  const enriched = enrichGovPointSections(points);
  const leafComparable: GovPoint[] = [];
  const skipped: Array<{ point: GovPoint; reason: string }> = [];
  const allPointIds = enriched.map((p) => p.point_id);

  for (const point of enriched) {
    let { comparable: ok, reason } = classifyGovPoint(point);

    if (ok && point.point_id.trim() === '2.') {
      ok = false;
      reason = '§2 umbrella skipped (compare sections 2.1, 2.2, …)';
    }

    if (ok && isNamedSectionSummaryPoint(point.point_id)) {
      ok = false;
      reason = 'section summary duplicate (use numbered sub-points)';
    }

    if (ok && isBareDefinitionLabel(point.point_id)) {
      ok = false;
      reason = 'definition list label (not a requirement section id)';
    }

    if (ok) leafComparable.push(point);
    else skipped.push({ point, reason: reason ?? 'informational' });
  }

  const rolled = rollupGovPointsToSections(leafComparable, allPointIds);
  return {
    comparable: rolled.comparable,
    skipped: [...skipped, ...rolled.skipped],
  };
}

/** Leaf-level compare: 2.1.1, 2.1.2, … — no section rollup; skip headers that have sub-points. */
export function filterComparableGovLeafPoints(points: GovPoint[]): {
  comparable: GovPoint[];
  skipped: Array<{ point: GovPoint; reason: string }>;
} {
  const enriched = enrichGovPointSections(points);
  const comparable: GovPoint[] = [];
  const skipped: Array<{ point: GovPoint; reason: string }> = [];
  const allPointIds = enriched.map((p) => p.point_id);

  for (const point of enriched) {
    let { comparable: ok, reason } = classifyGovPoint(point);

    if (ok && point.point_id.trim() === '2.') {
      ok = false;
      reason = '§2 umbrella skipped';
    }

    if (ok && isNamedSectionSummaryPoint(point.point_id)) {
      ok = false;
      reason = 'section summary duplicate (use numbered sub-points)';
    }

    if (ok && isBareDefinitionLabel(point.point_id)) {
      ok = false;
      reason = 'definition list label (not a requirement point id)';
    }

    if (ok && isNumericParentWithChildren(point.point_id, allPointIds)) {
      ok = false;
      reason = 'section header skipped (compare leaf sub-points only)';
    }

    if (ok) comparable.push(point);
    else skipped.push({ point, reason: reason ?? 'informational' });
  }

  comparable.sort((a, b) => compareGovPointIds(a.point_id, b.point_id));
  return { comparable, skipped };
}

/** Chapter from section heading, e.g. "4. NOTIFICATION…" → "4". */
export function chapterFromSection(section?: string): string | null {
  const s = (section ?? '').trim();
  const m = s.match(/^(\d+)(?:\.|\s|$)/);
  return m?.[1] ?? null;
}

/** Annex / list ids from PDF extract, e.g. "(i)", "(ii)". */
export function isRomanPointId(pointId: string): boolean {
  return /^\([ivxlcdm]+\)$/i.test(pointId.trim());
}

/** Annex sub-headings where Landing AI omits the parent "Annex 1" prefix. */
const ANNEX_SUBSECTION_RE =
  /^\d+\.\s+(?:Red Flag Indicators|Lessons learned)/i;

export function isAnnexSectionHeading(section?: string): boolean {
  return /^annex\s+\d+/i.test((section ?? '').trim());
}

export function isAnnexSubsectionHeading(section?: string): boolean {
  const s = (section ?? '').trim();
  if (!s || isAnnexSectionHeading(s)) return false;
  return ANNEX_SUBSECTION_RE.test(s);
}

/** Resolve annex chapter when section lost the "Annex 1" parent (common extract artifact). */
export function resolveAnnexChapter(
  section?: string,
  pointId?: string,
): string | null {
  const s = (section ?? '').trim();
  const annexInSection = s.match(/^(Annex\s+\d+)/i);
  if (annexInSection) return annexInSection[1];

  const id = (pointId ?? '').trim();
  if (isRomanPointId(id) || isAnnexSubsectionHeading(s)) {
    return 'Annex 1';
  }
  return null;
}

/**
 * Fix annex context on extracted points — e.g. section "2. Red Flag Indicators for PF"
 * becomes "Annex 1 · 2. Red Flag Indicators for PF" so it is not grouped under main §2.
 */
export function enrichGovPointSections(points: GovPoint[]): GovPoint[] {
  return points.map((point) => {
    const section = (point.section ?? '').trim();
    if (!section || isAnnexSectionHeading(section)) return point;

    const annex = resolveAnnexChapter(section, point.point_id);
    if (!annex || section.startsWith(`${annex} ·`)) return point;

    return { ...point, section: `${annex} · ${section}` };
  });
}

/**
 * Human-readable point id for UI — numeric ids unchanged; annex roman items
 * show as Annex-1.2.(i) (not bare (i) or main-body 2.(i)).
 */
export function formatGovPointDisplayId(point: GovPoint): string {
  const id = point.point_id.trim();
  const norm = normalizeNumericPointId(id);
  if (norm) return norm;

  if (isRomanPointId(id)) {
    const section = (point.section ?? '').trim();
    const annex = resolveAnnexChapter(section, id);
    const sub = section.match(/^(?:Annex\s+\d+\s*·\s*)?(\d+)\.\s+/i);
    if (annex && sub) {
      return `${annex.replace(/\s+/g, '-')}.${sub[1]}${id}`;
    }
    return id;
  }

  return id;
}

/** Chapter header — main body §N; annex chapters shown without § prefix. */
export function formatChapterLabel(chapter: string): string {
  const c = chapter.trim();
  if (/^annex\s+\d+/i.test(c)) return c;
  return `§${c}`;
}

/** Section bar label — numeric groups get § prefix; annex headings stay verbatim. */
export function formatSectionGroupLabel(key: string): string {
  const k = key.trim();
  if (!k) return k;
  if (/^annex\s+\d+/i.test(k)) return k;
  if (/^annex\s+\d+\s*·\s*/i.test(k)) return k;
  if (/^\d+\.\d+(?:\.\d+)*$/.test(k)) return `§${k}`;
  if (/^\d+$/.test(k)) return `§${k}`;
  return k;
}

/** Section group from heading, e.g. "2.4. Internal Controls" → "2.4", "4. NOTIFICATION…" → "4". */
export function sectionGroupFromSection(section?: string): string | null {
  const s = (section ?? '').trim();
  const m = s.match(/^(\d+\.\d+|\d+)(?:\.|\s)/);
  return m?.[1] ?? null;
}

/** Rollup key for non-numeric ids (Article 21(5)) under a numbered section heading. */
export function sectionRollupKeyFromHeading(point: GovPoint): string | null {
  if (normalizeNumericPointId(point.point_id)) return null;
  return sectionGroupFromSection(point.section);
}

/** Strip source prefix from merged gov ids, e.g. CD:3.2 → 3.2 */
export function stripGovPointPrefix(pointId: string): string {
  const idx = pointId.indexOf(':');
  return idx >= 0 ? pointId.slice(idx + 1).trim() : pointId.trim();
}

/** Top-level chapter id — main §2, §3, or Annex 1 (not annex "2." confused with §2). */
export function getChapterKey(pointId: string, section?: string): string | null {
  const id = stripGovPointPrefix(pointId);
  const annex = resolveAnnexChapter(section, id);
  if (annex) return annex;

  const norm = normalizeNumericPointId(id);
  if (norm) return norm.split('.')[0] ?? null;
  return chapterFromSection(section);
}

/** Mid-level section group, e.g. "2.4.1" → "2.4"; roman annex items use full section heading. */
export function getSectionGroupKey(pointId: string, section?: string): string | null {
  const id = stripGovPointPrefix(pointId);
  const norm = normalizeNumericPointId(id);
  if (norm) {
    const parts = norm.split('.');
    if (parts.length >= 3) return `${parts[0]}.${parts[1]}`;
    if (parts.length === 2) return norm;
    return null;
  }
  if (isRomanPointId(id)) {
    const s = (section ?? '').trim();
    return s || id;
  }
  if (isAnnexSubsectionHeading(section)) {
    return (section ?? '').trim();
  }
  return sectionGroupFromSection(section) ?? (id || null);
}

/** True when pointId equals prefix or is a child (2.4.1 matches "2" or "2.4"). */
export function pointMatchesPrefix(
  pointId: string,
  prefix: string,
  section?: string,
): boolean {
  const id = stripGovPointPrefix(pointId);
  const norm = normalizeNumericPointId(id);
  const p = (normalizeNumericPointId(prefix) ?? prefix.trim()).replace(/\.$/, '');
  if (!p) return false;
  if (norm) {
    return norm === p || norm.startsWith(`${p}.`);
  }
  const chapter = chapterFromSection(section);
  const secGroup = sectionGroupFromSection(section);
  const groupKey = getSectionGroupKey(pointId, section);
  const sec = (section ?? '').trim();
  if (groupKey === prefix || sec === prefix) return true;
  const annex = resolveAnnexChapter(section, pointId);
  if (annex && (prefix === annex || sec.startsWith(`${annex} ·`))) return true;
  return chapter === p || secGroup === p || Boolean(secGroup?.startsWith(`${p}.`));
}

export type GovPointChapterGroup = {
  chapter: string;
  points: GovPoint[];
  sections: Array<{ key: string; points: GovPoint[] }>;
};

/** Group comparable points by chapter (§2, §3) and mid-level section (§2.4, §2.1). */
export function groupGovPointsByChapter(points: GovPoint[]): GovPointChapterGroup[] {
  const chapterMap = new Map<string, Map<string, GovPoint[]>>();

  for (const point of points) {
    const chapter = getChapterKey(point.point_id, point.section);
    if (!chapter) continue;
    const sectionKey =
      getSectionGroupKey(point.point_id, point.section) ?? point.point_id.trim();

    if (!chapterMap.has(chapter)) chapterMap.set(chapter, new Map());
    const sectionMap = chapterMap.get(chapter)!;
    if (!sectionMap.has(sectionKey)) sectionMap.set(sectionKey, []);
    sectionMap.get(sectionKey)!.push(point);
  }

  return [...chapterMap.entries()]
    .sort(([a], [b]) => compareGovPointIds(a, b))
    .map(([chapter, sectionMap]) => ({
      chapter,
      points: points.filter(
        (p) => getChapterKey(p.point_id, p.section) === chapter,
      ),
      sections: [...sectionMap.entries()]
        .sort(([a], [b]) => compareGovPointIds(a, b))
        .map(([key, sectionPoints]) => ({ key, points: sectionPoints })),
    }));
}
