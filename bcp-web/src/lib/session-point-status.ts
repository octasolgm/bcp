/** Phase 1 (Landing AI) produced a result. */
export function isPhase1Complete(point: {
  landingMessage?: string | null;
  landingAiStatus?: string | null;
}): boolean {
  if (point.landingMessage?.trim()) return true;
  return point.landingAiStatus === 'completed';
}

/** Phase 2 (dual verify / Gemini) produced a result. */
export function isPhase2Complete(point: {
  llmMessage?: string | null;
  googleAiResult?: string | null;
  googleAiStatus?: string | null;
  dualVerifyStatus?: string | null;
}): boolean {
  if (point.llmMessage?.trim()) return true;
  if (point.googleAiResult?.trim()) return true;
  return point.dualVerifyStatus === 'completed' || point.googleAiStatus === 'completed';
}

/**
 * When phase 1 finished but phase 2 failed, treat the point as completed so results
 * stay visible while phase 2 can be retried separately.
 */
export function normalizeSessionPointStatus(point: {
  status: string;
  landingMessage?: string | null;
  llmMessage?: string | null;
  landingAiStatus?: string | null;
  googleAiStatus?: string | null;
  dualVerifyStatus?: string | null;
}): string {
  if (
    point.status === 'failed' &&
    isPhase1Complete(point) &&
    !isPhase2Complete(point)
  ) {
    return 'completed';
  }
  return point.status;
}

/** Phase 1 passed but phase 2 never completed — eligible for phase-2-only rerun. */
export function needsPhase2Rerun(point: {
  status?: string;
  landingMessage?: string | null;
  llmMessage?: string | null;
  errorMessage?: string | null;
  landingAiStatus?: string | null;
  googleAiStatus?: string | null;
  dualVerifyStatus?: string | null;
}): boolean {
  if (!isPhase1Complete(point)) return false;
  if (isPhase2Complete(point)) return false;
  const running =
    point.status === 'running' ||
    point.status === 'queued' ||
    point.status === 'processing' ||
    point.googleAiStatus === 'running' ||
    point.dualVerifyStatus === 'running';
  return !running;
}
