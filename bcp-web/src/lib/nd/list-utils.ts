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

/** Numeric-friendly compare for regulation point ids (e.g. §2.7, 2.10). */
export function comparePointNumber(a: string, b: string, dir: SortDir): number {
  const partsA = parsePointNumberParts(a);
  const partsB = parsePointNumberParts(b);
  const len = Math.max(partsA.length, partsB.length);
  for (let i = 0; i < len; i++) {
    const va = partsA[i] ?? -1;
    const vb = partsB[i] ?? -1;
    if (va !== vb) return sortMultiplier(dir) * (va - vb);
  }
  return compareText(a, b, dir);
}

function parsePointNumberParts(raw: string): number[] {
  const cleaned = raw.replace(/^§\s*/, '').trim();
  if (!cleaned) return [];
  const head = cleaned.split(/\s+/)[0] ?? cleaned;
  return head
    .split('.')
    .map((p) => Number.parseInt(p.replace(/\D/g, ''), 10))
    .filter((n) => Number.isFinite(n));
}
