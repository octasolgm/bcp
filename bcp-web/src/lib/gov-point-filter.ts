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
  const allPointIds = enriched.map((p) => resolveLogicalPointId(p as GovPointWithNumber));

  for (const point of enriched) {
    const logicalId = resolveLogicalPointId(point as GovPointWithNumber);
    const forClassify = { ...point, point_id: logicalId };
    let { comparable: ok, reason } = classifyGovPoint(forClassify);

    if (ok && isJunkExtractPointId(logicalId)) {
      ok = false;
      reason = 'section heading / part title (not a numbered clause)';
    }

    if (ok && logicalId.trim() === '2.') {
      ok = false;
      reason = '§2 umbrella skipped';
    }

    if (ok && isNamedSectionSummaryPoint(logicalId)) {
      ok = false;
      reason = 'section summary duplicate (use numbered sub-points)';
    }

    if (ok && isBareDefinitionLabel(logicalId)) {
      ok = false;
      reason = 'definition list label (not a requirement point id)';
    }

    if (ok && isNumericParentWithChildren(logicalId, allPointIds)) {
      ok = false;
      reason = 'section header skipped (compare leaf sub-points only)';
    }

    if (ok) comparable.push(point);
    else skipped.push({ point, reason: reason ?? 'informational' });
  }

  comparable.sort((a, b) =>
    compareGovPointIds(
      resolveLogicalPointId(a as GovPointWithNumber),
      resolveLogicalPointId(b as GovPointWithNumber),
    ),
  );
  return { comparable, skipped };
}

/**
 * Main-body numeric leaf points only (e.g. 2.1.1) — excludes annex roman (i)/(ii),
 * §1 introduction, and non-numeric ids. Use for library builder and regulation view.
 */
export function filterMainBodyNumericLeafPoints(points: GovPoint[]): GovPoint[] {
  const { comparable } = filterComparableGovLeafPoints(points);
  return comparable.filter((p) => {
    if (isRomanPointId(p.point_id)) return false;
    if (isAnnexPoint(p)) return false;
    if (/red flag indicators/i.test(p.title ?? '') || /red flag indicators/i.test(p.text)) {
      return false;
    }
    return normalizeNumericPointId(p.point_id) != null;
  });
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

function normalizePointNumberKey(pointNumber?: string | null): string {
  return (pointNumber ?? '').trim().replace(/\.$/, '').toLowerCase();
}

function contentFingerprint(text?: string | null): string {
  return (text ?? '').trim().toLowerCase().replace(/\s+/g, ' ');
}

function isNearDuplicateGovContent(a?: string | null, b?: string | null): boolean {
  const ta = contentFingerprint(a);
  const tb = contentFingerprint(b);
  if (!ta || !tb) return false;
  if (ta === tb) return true;
  const shorter = ta.length <= tb.length ? ta : tb;
  const longer = ta.length <= tb.length ? tb : ta;
  return longer.includes(shorter);
}

/**
 * When extract rows share a number (four "7.8" sub-topics), nest as 7.8.1, 7.8.2, …
 * Keep in sync with GovPointExtractNormalizer.AssignNestedIdsToDuplicateSiblings (bcp-api).
 */
export function assignNestedIdsToDuplicateSiblings(points: GovPoint[]): GovPoint[] {
  if (!points.length) return points;

  const groups = new Map<string, GovPoint[]>();
  for (const point of points) {
    const key = normalizePointNumberKey(point.point_id);
    if (!key) continue;
    const list = groups.get(key) ?? [];
    list.push(point);
    groups.set(key, list);
  }

  const singletonKeys = new Set(
    [...groups.entries()].filter(([, list]) => list.length === 1).map(([key]) => key),
  );
  const occupied = new Set<string>();
  for (const point of points) {
    const key = normalizePointNumberKey(point.point_id);
    if (key && singletonKeys.has(key)) occupied.add(key);
  }

  const reassigned = new Map<GovPoint, string>();
  for (const [key, group] of groups) {
    if (group.length <= 1) continue;

    const distinct: GovPoint[] = [];
    for (const item of group) {
      if (
        distinct.some(
          (existing) =>
            isNearDuplicateGovContent(existing.text, item.text) &&
            contentFingerprint(existing.title) === contentFingerprint(item.title),
        )
      ) {
        continue;
      }
      distinct.push(item);
    }

    if (distinct.length <= 1) continue;

    let suffix = 1;
    for (const item of distinct) {
      let nestedId = `${key}.${suffix}`;
      while (occupied.has(nestedId)) {
        suffix += 1;
        nestedId = `${key}.${suffix}`;
      }
      reassigned.set(item, nestedId);
      occupied.add(nestedId);
      suffix += 1;
    }
  }

  return points.map((point) => {
    const nested = reassigned.get(point);
    if (!nested) return point;
    return { ...point, point_id: nested };
  });
}

/**
 * When children exist (3.1.1) but parent (3.1) is missing, synthesize a section-heading row.
 * Keep in sync with GovPointExtractNormalizer.SynthesizeMissingParentPoints (bcp-api).
 */
export function synthesizeMissingParentGovPoints(points: GovPoint[]): GovPoint[] {
  if (!points.length) return points;

  const byKey = new Map<string, GovPoint>();
  for (const point of points) {
    const key = normalizePointNumberKey(point.point_id);
    if (key && !byKey.has(key)) byKey.set(key, point);
  }

  const parentsNeeded = new Set<string>();
  for (const key of byKey.keys()) {
    const parts = key.split('.');
    for (let depth = 1; depth < parts.length; depth += 1) {
      const parent = parts.slice(0, depth).join('.');
      if (!byKey.has(parent)) parentsNeeded.add(parent);
    }
  }

  if (!parentsNeeded.size) return points;

  const output = [...points];
  for (const parentId of [...parentsNeeded].sort(compareGovPointIds)) {
    const children = points.filter((p) => {
      const id = normalizeNumericPointId(p.point_id);
      return !!id?.startsWith(`${parentId}.`);
    });
    const sectionLine = resolveParentSectionLine(parentId, children);
    const title = sectionLine ? headingTitleFromSectionLine(sectionLine) : null;
    output.push({
      point_id: parentId,
      title: title ?? undefined,
      text: title ?? `Section ${parentId}`,
      section: sectionLine ?? undefined,
      point_type: 'informational',
    });
  }

  return output;
}

function resolveParentSectionLine(parentId: string, children: GovPoint[]): string | null {
  for (const child of children) {
    const fromSection = headingTitleFromSectionLine(child.section);
    if (fromSection) return `${parentId}. ${fromSection}`;

    const embedded =
      headingTitleEmbeddedForClause(parentId, child.section) ??
      headingTitleEmbeddedForClause(parentId, child.title) ??
      headingTitleEmbeddedForClause(parentId, child.text);
    if (embedded) return `${parentId}. ${embedded}`;
  }
  return null;
}
export function expandGovPointSubLeaves(points: GovPoint[]): GovPoint[] {
  const expanded: GovPoint[] = [];

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
      const leafId = `${norm}.${index + 1}`;
      expanded.push({
        ...point,
        point_id: leafId,
        title: inferBulletTitle(bullet) ?? point.title,
        text: bullet,
        section,
      });
    });
  }

  return expanded;
}

/**
 * Fix annex context on extracted points — e.g. section "2. Red Flag Indicators for PF"
 * becomes "Annex 1 · 2. Red Flag Indicators for PF" so it is not grouped under main §2.
 */
export function enrichGovPointSections(points: GovPoint[]): GovPoint[] {
  const expanded = expandGovPointSubLeaves(points);
  return expanded.map((point) => {
    const section = (point.section ?? '').trim();
    if (!section || isAnnexSectionHeading(section)) return point;

    const annex = resolveAnnexChapter(section, point.point_id);
    if (!annex || section.startsWith(`${annex} ·`)) return point;

    return { ...point, section: `${annex} · ${section}` };
  });
}

function isUuidLike(value: string): boolean {
  return /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value.trim());
}

/** Keep in sync with GovPointExtractNormalizer.IsJunkExtractPointId (bcp-api). */
export function isJunkExtractPointId(pointId?: string | null): boolean {
  const id = (pointId ?? '').trim();
  if (!id) return true;

  const junkSingleWord = new Set(
    ['complexity', 'controls', 'policies', 'procedures', 'typology', 'size/value', 'elements', 'element', 'governance'],
  );
  if (junkSingleWord.has(id.toLowerCase())) return true;

  const junkPatterns = [
    /^part\s+[ivxlc]+/i,
    /^part\s+v\b/i,
    /^elements?\s+of\s+an?\s+aml/i,
    /^the\s+elements\s+of/i,
    /^aml\/?cft\s+program\s+element/i,
    /^(first|second|third)\s+line\s+of\s+defense/i,
    /^scope\s+of\s+guidelines/i,
    /^part\s+iii\b/i,
    /^part\s+iv\b/i,
  ];
  if (junkPatterns.some((re) => re.test(id))) return true;
  if (/^element[s]?\b/i.test(id) && !/^\d/.test(id)) return true;
  if (id.includes(' - ') && !/^(aml[-/ ]?cft|article\s+\d+|annex\s+\d+)/i.test(id)) return true;
  if (/^part\s/i.test(id)) return true;

  return false;
}

/** Pull official clause id (e.g. 2.6.6) from section/title text — not Part I/II headings. */
export function extractNumericClauseRef(text?: string | null): string | null {
  const s = (text ?? '').trim();
  if (!s) return null;

  const lead = s.match(/^(?:§\s*)?(\d+\.\d+(?:\.\d+)*)\.?(?:\s|[.—–\-:)]|$)/);
  if (lead) return normalizeNumericPointId(lead[1]);

  const inner = s.match(/(?:^|[·—–\-]\s*)(?:§\s*)?(\d+\.\d+(?:\.\d+)*)\.?(?:\s|[.—–\-:)]|$)/);
  if (inner) return normalizeNumericPointId(inner[1]);

  return normalizeNumericPointId(s);
}

export type GovPointWithNumber = GovPoint & { pointNumber?: string };

/** Best id for classify/filter — prefers numeric clause, skips Part I junk and UUID keys. */
export function resolveLogicalPointId(point: GovPointWithNumber): string {
  const id = point.point_id.trim().replace(/\.$/, '');
  const idNorm = !isUuidLike(id) ? normalizeNumericPointId(id) : null;

  const num = (point.pointNumber ?? '').trim().replace(/\.$/, '');
  const numNorm = num && !isUuidLike(num) ? normalizeNumericPointId(num) : null;

  if (idNorm && numNorm) {
    const idDepth = idNorm.split('.').length;
    const numDepth = numNorm.split('.').length;
    if (idDepth !== numDepth) return idDepth > numDepth ? idNorm : numNorm;
    return idNorm;
  }
  if (idNorm) return idNorm;
  if (numNorm) return numNorm;
  if (num && !isJunkExtractPointId(num)) return num;

  if (!isUuidLike(id)) {
    if (!isJunkExtractPointId(id)) return id;
  }

  const fromSection =
    extractNumericClauseRef(point.section) ??
    extractNumericClauseRef((point.section ?? '').split('·')[0]);
  if (fromSection) return fromSection;

  const fromTitle = extractNumericClauseRef(point.title);
  if (fromTitle) return fromTitle;

  return num || id;
}

/** UI label for a regulation point — never prefer parent §3.1 over leaf 3.1.1; hide UUIDs and Part headings. */
export function resolveGovPointDisplayNumber(point: GovPointWithNumber): string {
  const logical = resolveLogicalPointId(point);
  const norm = normalizeNumericPointId(logical);
  if (norm) return norm;
  if (logical && !isUuidLike(logical) && !isJunkExtractPointId(logical)) {
    return logical.replace(/\.$/, '');
  }

  const fromText = extractNumericClauseRef(point.text?.split('\n')[0]);
  if (fromText) return fromText;

  const display = formatGovPointDisplayId({
    ...point,
    point_id: isUuidLike(point.point_id) ? logical : point.point_id,
  });
  if (display && !isUuidLike(display) && !isJunkExtractPointId(display)) {
    return display.replace(/\.$/, '');
  }

  return '';
}

/**
 * Human-readable point id for UI — numeric ids unchanged; annex roman items
 * show as Annex-1.2.(i) (not bare (i) or main-body 2.(i)).
 */
export function formatGovPointDisplayId(point: GovPoint): string {
  const id = point.point_id.trim();
  const section = (point.section ?? '').trim();
  const sectionHead = section.split('·')[0].trim();

  const idNorm = isUuidLike(id) ? null : normalizeNumericPointId(id);
  const sectionNorm =
    normalizeNumericPointId(sectionHead) ?? normalizeNumericPointId(section);

  if (idNorm) {
    if (!sectionNorm || idNorm.split('.').length >= sectionNorm.split('.').length) {
      return idNorm;
    }
  }

  if (sectionNorm) return sectionNorm;
  if (section && !normalizeNumericPointId(id)) return sectionHead || section;

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
export function formatChapterLabel(chapter: string, headingTitle?: string | null): string {
  const c = chapter.trim();
  let base: string;
  if (c === 'other') base = 'Other points';
  else if (c === 'intro') base = '§1 Introduction';
  else if (/^annex\s+\d+/i.test(c)) base = c;
  else base = `§${c}`;
  const title = headingTitle?.trim();
  if (title && c !== 'intro' && c !== 'other') return `${base} ${title}`;
  return base;
}

function displayChapterSortRank(chapter: string): number {
  const c = chapter.trim().toLowerCase();
  if (c === 'intro') return -2;
  if (c === '1') return -1;
  if (c === 'other') return 9_999;
  if (/^annex/.test(c)) return 5_000;
  const n = Number.parseInt(c, 10);
  if (!Number.isNaN(n)) return n;
  return 4_000;
}

/** Resolve UI chapter bucket — never drops points (intro, annex, orphans). */
export function resolveDisplayChapterKey(point: GovPoint): string {
  const chapter = getChapterKey(point.point_id, point.section);
  if (chapter) return chapter;

  if (isSectionOnePoint(point.point_id, point.section ?? '')) return '1';

  const { comparable, reason } = classifyGovPoint(point);
  if (!comparable) {
    const r = reason ?? '';
    if (
      r.includes('§1') ||
      r.includes('introduction') ||
      r.includes('purpose') ||
      r.includes('applicability') ||
      r.includes('informational narrative')
    ) {
      return 'intro';
    }
    if (r.includes('annex')) {
      return resolveAnnexChapter(point.section, point.point_id) ?? 'annex';
    }
  }

  const top = point.point_id.trim().match(/^(\d+)/);
  if (top) return top[1];

  return 'other';
}

/** Title text from a section line, e.g. "3.1. Summary of Minimum…" → "Summary of Minimum…". */
export function headingTitleFromSectionLine(section?: string | null): string | null {
  const s = (section ?? '').trim();
  if (!s) return null;

  const withoutPage = s.replace(/\s*·\s*p\.\s*\d+.*$/i, '').trim();
  const segments = withoutPage.split(/\s*·\s*/).map((part) => part.trim()).filter(Boolean);

  for (let i = segments.length - 1; i >= 0; i--) {
    const seg = segments[i];
    const m = seg.match(/^\d+(?:\.\d+)*\.?\s+(.+)$/);
    if (m?.[1]?.trim() && !isJunkSectionHeadingTitle(m[1])) {
      return m[1].trim();
    }
  }

  const whole = withoutPage.match(/^\d+(?:\.\d+)*\.?\s+(.+)$/);
  if (whole?.[1]?.trim() && !isJunkSectionHeadingTitle(whole[1])) {
    return whole[1].trim();
  }

  return null;
}

function isJunkSectionHeadingTitle(title: string): boolean {
  const t = title.trim();
  if (!t) return true;
  if (/^p\.\s*\d+/i.test(t)) return true;
  if (/^[·•\-–—]/.test(t)) return true;
  return false;
}

/** Find "3.1. Summary of…" embedded anywhere in a stored field. */
function headingTitleEmbeddedForClause(norm: string, text?: string | null): string | null {
  const s = (text ?? '').trim();
  if (!s) return null;
  const escaped = norm.replace(/\./g, '\\.');
  const patterns = [
    new RegExp(`\\b${escaped}\\.\\s+([^·\\n]{4,}?)(?:\\s*·|$)`, 'i'),
    new RegExp(`\\b${escaped}\\s+([A-Z][^·\\n]{4,}?)(?:\\s*·|$)`),
  ];
  for (const re of patterns) {
    const m = s.match(re);
    if (m?.[1]?.trim() && !isJunkSectionHeadingTitle(m[1])) {
      return m[1].trim();
    }
  }
  return null;
}

function resolvePointHeadingTitle(point: GovPoint | undefined, clauseKey: string): string | null {
  if (!point) return null;

  const title = (point.title ?? '').trim();
  if (title && !normalizeNumericPointId(title)) return title;

  const fromSection = headingTitleFromSectionLine(point.section);
  if (fromSection) return fromSection;

  const embedded =
    headingTitleEmbeddedForClause(clauseKey, point.section) ??
    headingTitleEmbeddedForClause(clauseKey, point.title) ??
    headingTitleEmbeddedForClause(clauseKey, point.text);
  if (embedded) return embedded;

  const text = (point.text ?? '').trim();
  if (text) {
    const firstLine = text.split(/\n/)[0]?.trim() ?? '';
    const fromFirst =
      headingTitleFromSectionLine(firstLine) ??
      headingTitleEmbeddedForClause(clauseKey, firstLine);
    if (fromFirst && !isJunkSectionHeadingTitle(fromFirst)) return fromFirst;

    const pointId = normalizeNumericPointId(point.point_id) ?? stripGovPointPrefix(point.point_id);
    if (
      pointId === clauseKey &&
      firstLine &&
      !isJunkSectionHeadingTitle(firstLine) &&
      !normalizeNumericPointId(firstLine) &&
      !/^[•\-*]\s/.test(firstLine) &&
      firstLine.length <= 120
    ) {
      return firstLine;
    }
  }

  return null;
}

/** Section heading title for §3.1-style group keys — from stored point title or section line. */
export function sectionHeadingTitleForKey(key: string, points: GovPoint[]): string | null {
  const norm = normalizeNumericPointId(key.trim());
  if (!norm) return null;

  const direct = points.find((p) => {
    const id = normalizeNumericPointId(p.point_id) ?? stripGovPointPrefix(p.point_id);
    return id === norm;
  });
  const directResolved = resolvePointHeadingTitle(direct, norm);
  if (directResolved) return directResolved;

  const candidates = new Set<string>();
  for (const p of points) {
    const id = normalizeNumericPointId(p.point_id) ?? stripGovPointPrefix(p.point_id);
    const isChild = !!id?.startsWith(`${norm}.`);
    const sg = sectionGroupFromSection(p.section);
    if (id !== norm && !isChild && sg !== norm) continue;

    const fromSection = headingTitleFromSectionLine(p.section);
    if (fromSection) candidates.add(fromSection);

    const embedded =
      headingTitleEmbeddedForClause(norm, p.section) ??
      headingTitleEmbeddedForClause(norm, p.title) ??
      headingTitleEmbeddedForClause(norm, p.text);
    if (embedded) candidates.add(embedded);
  }

  if (candidates.size === 1) return [...candidates][0];
  if (candidates.size > 1) {
    return [...candidates].sort((a, b) => b.length - a.length)[0];
  }

  return null;
}

/** Short label for analysing / result lists — prefer point title over body text. */
export function resolveGovPointListTitle(point: GovPointWithNumber): string {
  const title = (point.title ?? '').trim();
  if (title) return title;
  const displayId = resolveGovPointDisplayNumber(point);
  if (displayId) {
    const heading = sectionHeadingTitleForKey(displayId, [point]);
    if (heading) return heading;
  }
  const text = (point.text ?? '').trim();
  if (!text) return '';
  const first = text.split(/\n/)[0]?.trim() ?? text;
  return first.length > 120 ? `${first.slice(0, 117)}…` : first;
}

/** Section bar label — numeric groups get § prefix; annex headings stay verbatim. */
export function formatSectionGroupLabel(key: string, headingTitle?: string | null): string {
  const k = key.trim();
  if (!k) return k;
  let base: string;
  if (/^annex\s+\d+/i.test(k)) base = k;
  else if (/^annex\s+\d+\s*·\s*/i.test(k)) base = k;
  else if (/^\d+\.\d+(?:\.\d+)*$/.test(k)) base = `§${k}`;
  else if (/^\d+$/.test(k)) base = `§${k}`;
  else base = k;
  const title = headingTitle?.trim();
  if (title) return `${base} ${title}`;
  return base;
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
  if (/^1$/.test(id) || /^1\.$/.test(id)) return '1';
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

/** Group all points by chapter (intro, §1, §2, annex) and mid-level section (§2.4, §2.1). */
export function groupGovPointsByChapter(points: GovPoint[]): GovPointChapterGroup[] {
  const chapterMap = new Map<string, Map<string, GovPoint[]>>();

  for (const point of points) {
    const chapter = resolveDisplayChapterKey(point);
    const sectionKey =
      getSectionGroupKey(point.point_id, point.section) ?? (point.point_id.trim() || chapter);

    if (!chapterMap.has(chapter)) chapterMap.set(chapter, new Map());
    const sectionMap = chapterMap.get(chapter)!;
    if (!sectionMap.has(sectionKey)) sectionMap.set(sectionKey, []);
    sectionMap.get(sectionKey)!.push(point);
  }

  return [...chapterMap.entries()]
    .sort(([a], [b]) => {
      const rank = displayChapterSortRank(a) - displayChapterSortRank(b);
      return rank !== 0 ? rank : compareGovPointIds(a, b);
    })
    .map(([chapter, sectionMap]) => ({
      chapter,
      points: points.filter((p) => resolveDisplayChapterKey(p) === chapter),
      sections: [...sectionMap.entries()]
        .sort(([a], [b]) => compareGovPointIds(a, b))
        .map(([key, sectionPoints]) => ({ key, points: sectionPoints })),
    }));
}

/** Annex section labelling + duplicate-number nesting for picker display. */
export function enrichGovPointSectionsForPicker(points: GovPoint[]): GovPoint[] {
  const nested = assignNestedIdsToDuplicateSiblings(points);
  return nested.map((point) => {
    const section = (point.section ?? '').trim();
    if (!section || isAnnexSectionHeading(section)) return point;

    const annex = resolveAnnexChapter(section, point.point_id);
    if (!annex || section.startsWith(`${annex} ·`)) return point;

    return { ...point, section: `${annex} · ${section}` };
  });
}

/** Group regulation points for library picker — one UI row per stored point. */
export function groupGovPointsForPicker(points: GovPoint[]): GovPointChapterGroup[] {
  const enriched = enrichGovPointSectionsForPicker(points);
  const grouped = groupGovPointsByChapter(enriched);
  const inGroup = new Set<string>();
  for (const ch of grouped) {
    for (const p of ch.points) inGroup.add(p.point_id);
  }
  const orphans = enriched.filter((p) => !inGroup.has(p.point_id));
  if (orphans.length > 0) {
    grouped.push({
      chapter: 'other',
      points: orphans,
      sections: [{ key: 'other', points: orphans }],
    });
  }
  return grouped;
}

/** Full catalog for §heading resolution — includes synthesized parent section rows. */
export function buildGovPointDisplayCatalog(points: GovPoint[]): GovPoint[] {
  return synthesizeMissingParentGovPoints(enrichGovPointSectionsForPicker(points));
}

/** All extracted points grouped for display (§2, §3, §4, intro, annex). */
export function groupAllGovPointsForDisplay(points: GovPoint[]): GovPointChapterGroup[] {
  return groupGovPointsByChapter(enrichGovPointSections(points));
}
