import { formatStatus, statusBadgeClass } from "@/lib/utils";

export function StatusBadge({ status }: { status: string }) {
  return <span className={`badge ${statusBadgeClass(status)}`}>{formatStatus(status)}</span>;
}
