import type { GovRequirementPoint } from '../types/landing-ai.types';

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

/** Numeric hierarchy id, e.g. "2." → "2", "2.1.1" → "2.1.1". Bare "2" (definition label) → null. */
export function normalizeNumericPointId(pointId: string): string | null {
  const id = pointId.trim();
  if (!/^\d+(?:\.\d+)*\.?$/.test(id)) return null;
  if (!id.includes('.') && !id.endsWith('.')) return null;
  return id.replace(/\.$/, '');
}

/** Section headers like "2." or "2.4" — not bare definition labels like "2". */
export function isNumericSectionParentId(pointId: string): boolean {
  const id = pointId.trim();
  return /^\d+\.$/.test(id) || /^\d+\.\d+(?:\.\d+)*\.?$/.test(id);
}

/** Skip "2." when "2.1.1" exists — compare leaf points only. */
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

/** TOC-style duplicates from extract, e.g. "Sanctions Compliance Program - …". */
export function isNamedSectionSummaryPoint(pointId: string): boolean {
  const id = pointId.trim();
  return /^[A-Za-z]/.test(id) && id.includes(' - ') && !/^Article\b/i.test(id);
}

/** Extract artifact: bare "2" / "3" labels in definition lists — not section ids like 2.1. */
export function isBareDefinitionLabel(pointId: string): boolean {
  return /^\d+$/.test(pointId.trim());
}

/** §1 and all subpoints (1.1, 1.2, …) — compare starts at §2. */
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

/** Annexes and red-flag lists — not main guidance chapters §2–§4. */
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

/** Detect introduction / informational points that should not be compliance-compared. */
export function classifyGovPoint(point: GovRequirementPoint): GovPointClassification {
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

/** e.g. 2.1.1 → 2.1, 2.4.2 → 2.4 */
export function numericSubPointSectionKey(pointId: string): string | null {
  const norm = normalizeNumericPointId(pointId);
  if (!norm) return null;
  const parts = norm.split('.');
  if (parts.length >= 3) return `${parts[0]}.${parts[1]}`;
  return null;
}

/** Section header id (2.4) when sub-points like 2.4.1 exist. */
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

/** Strip legacy rollup/prompt lines accidentally stored in older cached points. */
export function cleanLegacyPromptFromRequirementText(text: string): string {
  return text
    .replace(/^Compare this entire government section[^\n]*\n\n/i, '')
    .replace(/^All sub-requirements below must be evaluated together:\s*\n\n/i, '')
    .replace(/^####\s+/gm, '')
    .trim();
}

/** Text shown in UI — extracted regulation only, never compare prompt. */
export function formatGovRequirementForDisplay(point: GovRequirementPoint): string {
  return cleanLegacyPromptFromRequirementText(point.text.trim());
}

function sectionTitleFromPoints(key: string, group: GovRequirementPoint[]): string {
  for (const p of group) {
    const section = (p.section ?? '').trim();
    const m = section.match(new RegExp(`^${key.replace('.', '\\.')}\\.\\s*(.+)$`, 'i'));
    if (m?.[1]) return m[1].trim();
  }
  const titled = group.find((p) => p.title?.trim());
  return titled?.title?.trim() ?? key;
}

function mergeSectionGroup(
  key: string,
  group: GovRequirementPoint[],
): GovRequirementPoint {
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
    text,
    section,
    page_hint: pageHint,
  };
}

/** Section group from heading, e.g. "4. NOTIFICATION…" → "4". */
export function sectionGroupFromSection(section?: string): string | null {
  const s = (section ?? '').trim();
  const m = s.match(/^(\d+\.\d+|\d+)(?:\.|\s)/);
  return m?.[1] ?? null;
}

/** Rollup key for non-numeric ids (Article 21(5)) under a numbered section heading. */
export function sectionRollupKeyFromHeading(point: GovRequirementPoint): string | null {
  if (normalizeNumericPointId(point.point_id)) return null;
  return sectionGroupFromSection(point.section);
}

/** Roll 2.1.1…2.1.6 into one compare unit 2.1 (same for 2.2, 2.4, 2.5, 2.6). */
export function rollupGovPointsToSections(
  points: GovRequirementPoint[],
  allPointIds: string[],
): {
  comparable: GovRequirementPoint[];
  skipped: Array<{ point: GovRequirementPoint; reason: string }>;
} {
  const groups = new Map<string, GovRequirementPoint[]>();
  const standalone: GovRequirementPoint[] = [];
  const skipped: Array<{ point: GovRequirementPoint; reason: string }> = [];

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

  const rolled: GovRequirementPoint[] = [];
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

/** First short phrase before semicolon — used as sub-point title when splitting bullets. */
function inferBulletTitle(bullet: string): string | null {
  const head = bullet.split(/[.;]/)[0]?.trim();
  if (!head || head.length > 72) return null;
  return head;
}

/** Split obligation bullet lists inside §X.Y section points into X.Y.1, X.Y.2, … */
function splitGovPointObligationBullets(text: string): string[] {
  const trimmed = text.trim();
  if (!trimmed) return [];

  if (/\s\*\s+/.test(trimmed) || /^\*\s+/m.test(trimmed)) {
    const parts = trimmed
      .split(/(?:^|\s)\*\s+/)
      .map((s) => s.trim())
      .filter(Boolean);
    if (parts.length < 2) return [];
    const [intro, ...bullets] = parts;
    if (bullets.length < 2) return [];
    if (/:\s*$/.test(intro)) return bullets;
    if (intro.length > 40) {
      return bullets.map((b, i) => (i === 0 ? `${intro}: ${b}` : b));
    }
    return bullets;
  }

  const colonLabels = trimmed.split(
    /(?=\b(?:Periodic|Ad hoc|Re-screening)\b[^:]*:)/i,
  );
  const labeled = colonLabels
    .map((s) => s.trim())
    .filter((s) => /^(?:Periodic|Ad hoc|Re-screening)\b/i.test(s) && s.length > 20);
  if (labeled.length >= 2) return labeled;

  const dashTail = trimmed.match(/:\s+(-\s+.+)$/s);
  if (dashTail?.index !== undefined) {
    const preamble = trimmed.slice(0, dashTail.index).trim();
    const list = trimmed.slice(dashTail.index + 1).trim();
    const items = list
      .split(/\s+-\s+/)
      .map((s) => s.replace(/^-\s*/, '').trim())
      .filter(Boolean);
    if (items.length >= 2) {
      return items.map((item) => `${preamble}: ${item}`);
    }
  }

  return [];
}

/**
 * Expand §X.Y extract blobs with inline bullet lists into leaf ids (3.1 → 3.1.1 …).
 * Keep in sync with apps/reguliq-dotnet/client/reguliq-web/src/lib/gov-point-filter.ts
 */
export function expandGovPointSubLeaves(
  points: GovRequirementPoint[],
): GovRequirementPoint[] {
  const expanded: GovRequirementPoint[] = [];

  for (const point of points) {
    const norm = normalizeNumericPointId(point.point_id);
    if (!norm || norm.split('.').length !== 2) {
      expanded.push(point);
      continue;
    }

    const bullets = splitGovPointObligationBullets(point.text);
    if (bullets.length < 2) {
      expanded.push(point);
      continue;
    }

    const section =
      (point.section ?? '').trim() ||
      (point.title ? `${norm}. ${point.title}` : `${norm}.`);

    bullets.forEach((bullet, index) => {
      expanded.push({
        ...point,
        point_id: `${norm}.${index + 1}`,
        title: inferBulletTitle(bullet) ?? point.title,
        text: bullet,
        section,
      });
    });
  }

  return expanded;
}

export function filterComparableGovPoints(points: GovRequirementPoint[]): {
  comparable: GovRequirementPoint[];
  skipped: Array<{ point: GovRequirementPoint; reason: string }>;
} {
  const source = expandGovPointSubLeaves(points);
  const leafComparable: GovRequirementPoint[] = [];
  const skipped: Array<{ point: GovRequirementPoint; reason: string }> = [];
  const allPointIds = source.map((p) => p.point_id);

  for (const point of source) {
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
export function filterComparableGovLeafPoints(points: GovRequirementPoint[]): {
  comparable: GovRequirementPoint[];
  skipped: Array<{ point: GovRequirementPoint; reason: string }>;
} {
  const source = expandGovPointSubLeaves(points);
  const comparable: GovRequirementPoint[] = [];
  const skipped: Array<{ point: GovRequirementPoint; reason: string }> = [];
  const allPointIds = source.map((p) => p.point_id);

  for (const point of source) {
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
