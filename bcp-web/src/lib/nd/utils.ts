import type { PointSnapshot } from "./types";

export function formatDate(iso?: string | null): string {
  if (!iso) return "—";
  return new Date(iso).toLocaleString();
}

export function formatBytes(bytes?: number): string {
  if (!bytes) return "—";
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

export function parsePointSnapshot(raw: string): PointSnapshot {
  try {
    const parsed = JSON.parse(raw) as PointSnapshot & { pdfPage?: number | string | null };
    if (typeof parsed.pdfPage === 'string') {
      const n = Number.parseInt(parsed.pdfPage, 10);
      parsed.pdfPage = Number.isFinite(n) && n > 0 ? n : null;
    }
    return parsed;
  } catch {
    return {};
  }
}

export function statusBadgeClass(status: string): string {
  const map: Record<string, string> = {
    completed: "badge-green",
    compliant: "badge-green",
    checker_approved: "badge-green",
    reviewer_approved: "badge-green",
    pending: "badge-gray",
    draft: "badge-gray",
    processing: "badge-blue",
    running: "badge-blue",
    submitted_for_review: "badge-blue",
    partial_compliant: "badge-amber",
    dual_verify_failed: "badge-amber",
    failed: "badge-red",
    non_compliant: "badge-red",
    pulled_back: "badge-red",
    error: "badge-red",
  };
  return map[status] ?? "badge-gray";
}

export function formatStatus(status: string): string {
  return status.replace(/_/g, " ");
}
