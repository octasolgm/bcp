/** Normalize analysis run status for comparisons and routing. */
export function normalizeRunStatus(status: string | null | undefined): string {
  return (status ?? '').trim().toLowerCase();
}

export function isPulledBackRun(status: string | null | undefined): boolean {
  return normalizeRunStatus(status) === 'pulled_back';
}
