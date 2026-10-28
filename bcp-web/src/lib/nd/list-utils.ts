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

/** Strips every trailing file extension, not just one — some stored names carry a doubled
 * extension (e.g. "CandNM-.pdf.pdf") from a re-upload/versioning step tacking .pdf onto a name
 * that already had one. */
function stripAllExtensions(name: string): string {
  let s = name;
  let next = s.replace(/\.[a-z0-9]{1,5}$/, '');
  while (next !== s) {
    s = next;
    next = s.replace(/\.[a-z0-9]{1,5}$/, '');
  }
  return s;
}

/** True when a document's original filename is worth showing next to its title — false when it's
 * just the same name with a " (vN)" version suffix and/or a doubled extension tacked on (or the
 * exact same string), which reads as the name printed twice rather than as extra information. */
const FORMAT_LABEL_BY_EXT: Record<string, string> = {
  pdf: 'PDF',
  doc: 'DOC',
  docx: 'DOC',
  xls: 'XLS',
  xlsx: 'XLS',
  xlsm: 'XLS',
  ppt: 'PPT',
  pptx: 'PPT',
  txt: 'TXT',
  csv: 'CSV',
  rtf: 'RTF',
  odt: 'ODT',
  ods: 'ODS',
};

/** Short pill label (PDF, DOC, XLS, …) from a file name's extension. */
export function documentFormatLabelFromFileName(fileName: string | null | undefined): string | null {
  if (!fileName?.trim()) return null;
  const m = fileName.trim().match(/\.([a-z0-9]{1,5})$/i);
  if (!m) return null;
  const ext = m[1].toLowerCase();
  return FORMAT_LABEL_BY_EXT[ext] ?? ext.toUpperCase();
}

/** Best stored name to infer format for catalog rows (internal / regulation documents). */
export function catalogDocumentFormatLabel(doc: {
  originalFileName?: string | null;
  sourceOriginalFileName?: string | null;
  landingAiFileName?: string | null;
  title?: string | null;
  name?: string | null;
  isManual?: boolean;
}): string | null {
  if (doc.isManual) return null;
  const candidates = [
    doc.originalFileName,
    doc.landingAiFileName,
    doc.sourceOriginalFileName,
    doc.title ?? doc.name,
  ];
  for (const name of candidates) {
    const label = documentFormatLabelFromFileName(name);
    if (label) return label;
  }
  return null;
}

export function isDistinctOriginalFileName(title: string | null | undefined, originalFileName: string | null | undefined): boolean {
  if (!originalFileName) return false;
  const t = (title ?? '').trim().toLowerCase();
  const o = originalFileName.trim().toLowerCase();
  if (!t || !o) return !!o;
  if (t === o) return false;
  const oWithoutVersion = o.replace(/\s*\(v\d+\)(?=\.[a-z0-9]+$|$)/, '').trim();
  const tWithoutExt = stripAllExtensions(t);
  const oWithoutExt = stripAllExtensions(oWithoutVersion);
  return tWithoutExt !== oWithoutExt;
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
