import type { BcpwebSeverity } from '@/types';
import { severityColor, severityLabel } from '@/types';
import { cn } from '@/lib/utils';

interface SeverityBadgeProps {
  severity: BcpwebSeverity;
  className?: string;
}

/** Severity pill badge */
export function SeverityBadge({ severity, className }: SeverityBadgeProps) {
  return (
    <span
      className={cn(
        'inline-flex rounded-full border px-2 py-0.5 text-xs font-medium',
        severityColor(severity),
        className,
      )}
    >
      {severityLabel(severity)}
    </span>
  );
}
