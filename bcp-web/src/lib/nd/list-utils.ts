export type SortDir = 'asc' | 'desc';

export function matchesSearch(
  query: string,
  values: (string | null | undefined)[],
): boolean {
  const q = query.trim().toLowerCase();
  if (!q) return true;
  return values.some((v) => (v ?? '').toLowerCase().includes(q));
}

export function sortMultiplier(dir: SortDir): number {
  return dir === 'asc' ? 1 : -1;
}

export function compareText(a: string, b: string, dir: SortDir): number {
  return sortMultiplier(dir) * a.localeCompare(b);
}

export function compareNumber(a: number, b: number, dir: SortDir): number {
  return sortMultiplier(dir) * (a - b);
}

export function compareDateIso(a: string, b: string, dir: SortDir): number {
  return sortMultiplier(dir) * (Date.parse(a) - Date.parse(b));
}

export function nextSortState<T extends string>(
  currentCol: T,
  clickedCol: T,
  currentDir: SortDir,
  defaultDescCol?: T,
): { column: T; dir: SortDir } {
  if (currentCol === clickedCol) {
    return { column: clickedCol, dir: currentDir === 'asc' ? 'desc' : 'asc' };
  }
  return {
    column: clickedCol,
    dir: defaultDescCol && clickedCol === defaultDescCol ? 'desc' : 'asc',
  };
}

export function sortIndicator(activeCol: string, col: string, dir: SortDir): string {
  return activeCol === col ? (dir === 'asc' ? '↑' : '↓') : '';
}

export function hasListFilters(...filters: (string | boolean | undefined | null)[]): boolean {
  return filters.some((f) => (typeof f === 'string' ? f.trim().length > 0 : Boolean(f)));
}

/** Numeric-friendly compare for regulation point ids (e.g. §2.7, 2.10, 6.18-a). */
export function comparePointNumber(a: string, b: string, dir: SortDir): number {
  const partsA = parsePointRefTokens(a);
  const partsB = parsePointRefTokens(b);
  const len = Math.max(partsA.length, partsB.length);
  for (let i = 0; i < len; i++) {
    const ta = partsA[i] ?? { num: -1, suffix: '' };
    const tb = partsB[i] ?? { num: -1, suffix: '' };
    if (ta.num >= 0 && tb.num >= 0) {
      if (ta.num !== tb.num) return sortMultiplier(dir) * (ta.num - tb.num);
      const suffixCmp = ta.suffix.localeCompare(tb.suffix);
      if (suffixCmp !== 0) return sortMultiplier(dir) * suffixCmp;
      continue;
    }
    if (ta.num >= 0) return -sortMultiplier(dir);
    if (tb.num >= 0) return sortMultiplier(dir);
  }
  return sortMultiplier(dir) * a.trim().localeCompare(b.trim(), undefined, {
    numeric: true,
    sensitivity: 'base',
  });
}

export function sortByPointRef<T>(
  items: T[],
  key: (item: T) => string,
  dir: SortDir = 'asc',
): T[] {
  return [...items].sort((x, y) => comparePointNumber(key(x), key(y), dir));
}

type PointRefToken = { num: number; suffix: string };

function parseSegment(segment: string): PointRefToken {
  const m = segment.match(/^(\d+)([a-z]*)$/i);
  if (m) {
    return { num: Number.parseInt(m[1], 10), suffix: (m[2] ?? '').toLowerCase() };
  }
  const digits = segment.replace(/\D/g, '');
  if (digits) return { num: Number.parseInt(digits, 10), suffix: '' };
  return { num: -1, suffix: segment.toLowerCase() };
}

function parsePointRefTokens(raw: string): PointRefToken[] {
  const cleaned = raw.replace(/^§\s*/, '').trim();
  const head = (cleaned.split(/\s+/)[0] ?? cleaned).replace(/\.$/, '');
  if (!head) return [];

  if (head.includes('.')) {
    return head.split('.').filter(Boolean).map(parseSegment);
  }

  if (head.includes('-')) {
    const dashParts = head.split('-').filter(Boolean);
    if (dashParts.length > 1 && dashParts.every((p) => /^\d+[a-z]*$/i.test(p))) {
      return dashParts.map(parseSegment);
    }
  }

  const numPrefix = head.match(/^(\d+(?:\.\d+)*)/);
  if (!numPrefix) return [parseSegment(head)];

  const tokens = numPrefix[1]
    .split('.')
    .filter(Boolean)
    .map((p) => ({ num: Number.parseInt(p, 10), suffix: '' }));

  const suffixPart = head.slice(numPrefix[0].length).replace(/^[-.]+/, '');
  if (suffixPart.length > 0) {
    const seg = parseSegment(suffixPart);
    if (seg.num >= 0 || seg.suffix) tokens.push(seg);
  }

  return tokens;
}
