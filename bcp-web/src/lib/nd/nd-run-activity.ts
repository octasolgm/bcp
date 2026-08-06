import { isActiveDocumentRun } from '../../app/services/active-analysis-sessions.service';

const REVIEW_WORKFLOW = new Set([
  'submitted_for_review',
  'checker_approved',
  'reviewer_approved',
]);

/** ND run still scoring points (matches sidebar nav-counts / in-progress page). */
export function isNdRunProcessing(run: {
  status: string;
  processedPointsCount?: number;
  totalPointsCount?: number;
  dualVerifyFailedCount?: number;
  createdAt?: string;
  updatedAt?: string;
  runningPoints?: number;
  isActive?: boolean;
}): boolean {
  if (run.isActive === false) return false;
  if (run.isActive === true) return true;

  const st = (run.status || '').toLowerCase();
  if (st === 'deleted') return false;
  if (REVIEW_WORKFLOW.has(st)) return false;

  const updatedAt = run.updatedAt ?? run.createdAt;

  return isActiveDocumentRun({
    status: run.status,
    completedPoints: run.processedPointsCount,
    failedPoints: run.dualVerifyFailedCount,
    pointCount: run.totalPointsCount,
    updatedAt,
    runningPoints: run.runningPoints,
  });
}
