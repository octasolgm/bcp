import { officialCbuaeViewerPage } from './cbuae-official-pages';

export type RegulationPageResolveOpts = {
  docName?: string | null;
  pointNumber?: string | null;
};

/** Resolve a PDF viewer page number from extract metadata. */
export function resolveRegulationPdfPage(
  pageReference?: string | null,
  pdfPage?: number | null,
  opts?: RegulationPageResolveOpts,
): number | null {
  const official = officialCbuaeViewerPage(opts?.docName, opts?.pointNumber);
  if (official != null) return official;
  // Snapshot pdfPage is already a viewer page. Search and the points list use
  // stored pageReference only (live markdown re-resolve can land on a later match).
  if (pdfPage != null && pdfPage > 0) return pdfPage;
  return storedPageToViewerPage(parsePdfPageFromReference(pageReference));
}

/** Older extracts stored the real viewer page plus one. */
export function storedPageToViewerPage(page: number | null | undefined): number | null {
  if (page == null || page <= 0) return null;
  return Math.max(1, page - 1);
}

export function parsePdfPageFromReference(reference?: string | null): number | null {
  const trimmed = (reference ?? '').trim();
  if (!trimmed) return null;
  const match = trimmed.match(/(?:page|p\.|pp\.)\s*(\d+)/i);
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
  opts?: RegulationPageResolveOpts,
): string | null {
  const page = resolveRegulationPdfPage(pageReference, pdfPage, opts);
  if (page != null) return `p. ${page}`;
  const ref = (pageReference ?? '').trim();
  return ref || null;
}
