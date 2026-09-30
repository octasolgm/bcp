import { normalizeInternalSectionRef } from './internal-section-group';
import { sanitizePolicySectionText } from './policy-section-text';

/** Split stored clause/section text into a header line (number + title) and body prose. */
export function splitSectionDisplayText(
  sectionText: string,
  sectionRef: string,
): { header: string; body: string } {
  const ref = normalizeInternalSectionRef(sectionRef).trim();
  const clean = sanitizePolicySectionText(sectionText);
  if (!clean) return { header: ref, body: '' };

  const newline = clean.indexOf('\n');
  const firstLine = (newline === -1 ? clean : clean.slice(0, newline)).trim();
  const rest = newline === -1 ? '' : clean.slice(newline + 1).trim();

  if (firstLine && lineLooksLikeSectionHeading(firstLine, ref)) {
    return { header: firstLine, body: rest };
  }

  return { header: ref, body: clean };
}

export function splitGovPointDisplayText(
  pointId: string,
  text: string,
  title?: string | null,
): { header: string; body: string } {
  const id = pointId.trim();
  const bodyText = sanitizePolicySectionText(text);
  const titleTrim = (title ?? '').trim();

  if (bodyText) {
    const fromText = splitSectionDisplayText(bodyText, id);
    if (fromText.header !== id || !titleTrim) return fromText;
  }

  if (titleTrim) {
    const header =
      titleTrim.toLowerCase().startsWith(id.toLowerCase()) || /^\d+(?:\.\d+)*\.?\s/.test(titleTrim)
        ? titleTrim
        : `${id}. ${titleTrim}`;
    return { header, body: bodyText };
  }

  if (!bodyText) return { header: id, body: '' };

  return { header: id, body: bodyText };
}

function lineLooksLikeSectionHeading(line: string, ref: string): boolean {
  const l = line.toLowerCase();
  const r = ref.toLowerCase();
  if (l === r || l.startsWith(`${r}.`) || l.startsWith(`${r} `) || l.startsWith(`${r}:`)) {
    return true;
  }

  const annexRef = r.replace(/^annex\s+/i, '').trim();
  if (annexRef && (l.startsWith(`${annexRef}.`) || l.startsWith(`${annexRef} `))) return true;

  const numbered = /^(\d+(?:\.\d+)*)\.?\s+\S/u.exec(line);
  if (numbered && (r === numbered[1] || r.endsWith(`.${numbered[1]}`) || r === numbered[1])) {
    return true;
  }

  if (
    line.length <= 160 &&
    /^(?:\d+(?:\.\d+)*|(?:article|section|rule|clause|chapter|annex)\s+\S+)\.?\s+[A-Za-z]/i.test(line)
  ) {
    return true;
  }

  return false;
}
