/** Catalog Pages column: total PDF pages when known. Em dash while hidden or missing. */
export function catalogPdfPageLabel(
  pageCount: number | null | undefined,
  hide = false,
): string {
  if (hide) return '—';
  const pages = pageCount ?? 0;
  return pages > 0 ? `${pages}` : '—';
}
