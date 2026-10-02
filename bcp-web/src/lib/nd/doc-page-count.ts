/** Catalog Pages column: total PDF pages when known. Em dash while hidden or missing. */
export function catalogPdfPageLabel(
  pageCount: number | null | undefined,
  hide = false,
): string {
  if (hide) return '—';
  const pages = pageCount ?? 0;
  return pages > 0 ? `${pages}` : '—';
}

/** Side panel subtitle, e.g. "63 pages". */
export function detailPanelPagesPhrase(pageCount: number | null | undefined): string | null {
  const pages = pageCount ?? 0;
  if (pages <= 0) return null;
  return `${pages} page${pages === 1 ? '' : 's'}`;
}
