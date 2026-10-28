/** Strip leading section mark for comparisons. */
export function stripSectionMark(value: string): string {
  return value.replace(/^§\s*/, '').trim();
}

function clauseNumPrefixPattern(clauseNum: string): RegExp | null {
  const num = stripSectionMark(clauseNum);
  if (!num) return null;
  const numEsc = num.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  return new RegExp(`^§?\\s*${numEsc}(?:\\s*[—–\\-:.]\\s*|\\s+)`, 'i');
}

/**
 * Title line for clause rail cards: one clause number in the header row, then this heading
 * (without repeating the number when the stored title is "3.3 — Purpose" or "§3.3 Purpose").
 */
export function resolveClauseRailHeading(clauseNum: string, rawTitle?: string | null): string {
  const num = stripSectionMark(clauseNum);
  let title = stripSectionMark((rawTitle ?? '').trim());
  if (!title || !num) return title && title !== num ? title : '';

  if (title === num) return '';

  const prefixRe = clauseNumPrefixPattern(num);
  if (prefixRe?.test(title)) {
    title = title.replace(prefixRe, '').trim();
  }

  if (!title || title === num) return '';
  return title;
}

export function truncateRailClauseText(text: string, maxLen = 140): string {
  const t = text.trim();
  if (!t) return '—';
  return t.length > maxLen ? `${t.slice(0, maxLen)}…` : t;
}

/** Drop clause number / title already shown above the excerpt line. */
export function railClauseExcerpt(
  clauseNum: string,
  heading: string,
  rawText: string,
  maxLen = 140,
): string {
  let t = rawText.trim();
  if (!t) return '—';

  const prefixRe = clauseNumPrefixPattern(clauseNum);
  if (prefixRe?.test(t)) {
    t = t.replace(prefixRe, '').trim();
  }
  if (heading) {
    const h = heading.trim();
    if (h && t.toLowerCase().startsWith(h.toLowerCase())) {
      t = t.slice(h.length).replace(/^[\s—–\-:.]+/, '').trim();
    }
  }

  return truncateRailClauseText(t || rawText.trim(), maxLen);
}

export type ClauseRailCardFields = {
  clauseNum: string;
  /** Resolved title under the clause number (empty when unknown). */
  heading: string;
  clauseText: string;
  /** Raw title passed to the card `@Input()` (may match stored title or first line of text). */
  titleLine: string;
};

/**
 * Gap rows often store one line like "3.3 Title (Article 27) The law requires…".
 * Pull a display title before the citation paren or before "The …" body.
 */
export function splitInferredTitleFromBody(
  clauseNum: string,
  line: string,
): { titleSource: string; remainder: string } | null {
  const num = stripSectionMark(clauseNum);
  let t = line.trim();
  if (!t) return null;

  const prefixRe = clauseNumPrefixPattern(num);
  if (prefixRe?.test(t)) t = t.replace(prefixRe, '').trim();
  if (!t) return null;

  const paren = t.match(/^(.+?)\s+\(/);
  if (paren && paren[1].trim().length >= 6) {
    const titleWords = paren[1].trim();
    const remainder = t.slice(titleWords.length).trim();
    return { titleSource: `${num} ${titleWords}`, remainder: remainder || t };
  }

  const theSplit = t.match(/^(.+?)\s+(The\s+[A-Z])/);
  if (theSplit && theSplit[1].trim().length >= 6) {
    const titleWords = theSplit[1].trim();
    const remainder = t.slice(titleWords.length).trim();
    return { titleSource: `${num} ${titleWords}`, remainder };
  }

  return null;
}

/** Prefer stored title; gap report rows often only have the number in `title` and the name in body text. */
export function pickClauseRailTitleSource(
  clauseNum: string,
  title?: string | null,
  clauseText?: string | null,
): string {
  const num = stripSectionMark(clauseNum);
  const stored = (title ?? '').trim();
  if (stored && resolveClauseRailHeading(num, stored)) return stored;

  const text = (clauseText ?? '').trim();
  if (!text) return stored;

  const firstLine = text.split('\n').map((l) => l.trim()).find(Boolean) ?? text;
  const split = splitInferredTitleFromBody(num, firstLine);
  if (split) return split.titleSource;

  if (resolveClauseRailHeading(num, firstLine)) return firstLine;

  return stored || firstLine;
}

/** When title is inferred from a single-line body, use the tail for the excerpt. */
export function clauseRailExcerptSource(
  clauseNum: string,
  title?: string | null,
  clauseText?: string | null,
): string {
  const raw = (clauseText ?? '').trim();
  if (!raw) return raw;

  const num = stripSectionMark(clauseNum);
  const stored = (title ?? '').trim();
  if (stored && resolveClauseRailHeading(num, stored)) return raw;

  const firstLine = raw.split('\n').map((l) => l.trim()).find(Boolean) ?? raw;
  const split = splitInferredTitleFromBody(num, firstLine);
  if (split?.remainder) {
    const restLines = raw.includes('\n') ? raw.slice(raw.indexOf('\n')).trim() : '';
    return restLines ? `${split.remainder}\n${restLines}`.trim() : split.remainder;
  }
  return raw;
}

/** Single builder for gap analysis, analyse-regul rails, and admin preview. */
export function buildClauseRailCardFields(opts: {
  clauseNum: string;
  title?: string | null;
  clauseText?: string | null;
  maxTextLen?: number;
}): ClauseRailCardFields {
  const clauseNum = stripSectionMark(opts.clauseNum);
  const rawText = (opts.clauseText ?? '').trim();
  const titleLine = pickClauseRailTitleSource(clauseNum, opts.title, rawText);
  const heading = resolveClauseRailHeading(clauseNum, titleLine);
  const excerptSource = clauseRailExcerptSource(clauseNum, opts.title, rawText);
  const clauseText = railClauseExcerpt(clauseNum, heading, excerptSource, opts.maxTextLen);
  return { clauseNum, heading, clauseText, titleLine };
}
