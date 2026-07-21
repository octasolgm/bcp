/** Resolve a PDF page number from extract metadata. */
export function resolveRegulationPdfPage(
  pageReference?: string | null,
  pdfPage?: number | null,
): number | null {
  if (pdfPage != null && pdfPage > 0) return pdfPage;
  return parsePdfPageFromReference(pageReference);
}

export function parsePdfPageFromReference(reference?: string | null): number | null {
  const trimmed = (reference ?? '').trim();
  if (!trimmed) return null;
  const match = trimmed.match(/(?:page|p\.?|pp\.?)\s*(\d+)/i);
  if (match) {
    const n = Number.parseInt(match[1], 10);
    if (Number.isFinite(n) && n > 0) return n;
  }
  if (/^\d+$/.test(trimmed)) {
    const n = Number.parseInt(trimmed, 10);
    if (n > 0) return n;
  }
  return null;
}

export function formatPointPageRef(
  pageReference?: string | null,
  pdfPage?: number | null,
): string | null {
  const page = resolveRegulationPdfPage(pageReference, pdfPage);
  if (page != null) return `p. ${page}`;
  const ref = (pageReference ?? '').trim();
  return ref || null;
}
