/**
 * Per-workspace date display. UAE is the platform default; a business can pick its own country's
 * convention instead (set from Workspaces admin, or by that workspace's own admin). Keep this list in
 * sync with WorkspacesController.ValidDateFormatRegions (bcp-api) — add to both together.
 */
export type NdDateFormatRegion = 'uae' | 'pakistan' | 'usa' | 'uk' | 'india' | 'iso';

export const ND_DATE_FORMAT_OPTIONS: { value: NdDateFormatRegion; label: string; example: string }[] = [
  { value: 'uae', label: 'UAE (DD/MM/YYYY)', example: '27/09/2026' },
  { value: 'pakistan', label: 'Pakistan (DD/MM/YYYY)', example: '27/09/2026' },
  { value: 'usa', label: 'USA (MM/DD/YYYY)', example: '09/27/2026' },
  { value: 'uk', label: 'UK (DD/MM/YYYY)', example: '27/09/2026' },
  { value: 'india', label: 'India (DD/MM/YYYY)', example: '27/09/2026' },
  { value: 'iso', label: 'ISO (YYYY-MM-DD)', example: '2026-09-27' },
];

const DEFAULT_REGION: NdDateFormatRegion = 'uae';

function normalizeRegion(region: string | null | undefined): NdDateFormatRegion {
  const r = (region ?? '').toLowerCase();
  return ND_DATE_FORMAT_OPTIONS.some((o) => o.value === r) ? (r as NdDateFormatRegion) : DEFAULT_REGION;
}

function datePart(iso: string, region: NdDateFormatRegion): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '—';
  const dd = String(d.getDate()).padStart(2, '0');
  const mm = String(d.getMonth() + 1).padStart(2, '0');
  const yyyy = d.getFullYear();
  switch (region) {
    case 'usa':
      return `${mm}/${dd}/${yyyy}`;
    case 'iso':
      return `${yyyy}-${mm}-${dd}`;
    default:
      return `${dd}/${mm}/${yyyy}`;
  }
}

function timePart(iso: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '';
  return d.toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' });
}

/** Date only, in the given (or default) region's convention. */
export function formatNdDate(iso: string | null | undefined, region?: string | null): string {
  if (!iso) return '—';
  return datePart(iso, normalizeRegion(region));
}

/** Date + time, in the given (or default) region's convention. */
export function formatNdDateTime(iso: string | null | undefined, region?: string | null): string {
  if (!iso) return '—';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '—';
  return `${datePart(iso, normalizeRegion(region))}, ${timePart(iso)}`;
}
