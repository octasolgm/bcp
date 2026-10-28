/** Matches server <see cref="LocalStructuralCoverage.LowCoverageThreshold"/>. */
export const STRUCTURAL_COVERAGE_TARGET = 0.98;

export function formatStructuralCoveragePct(ratio: number | null | undefined): string | null {
  if (ratio == null || !Number.isFinite(ratio)) return null;
  return `${(Math.round(ratio * 1000) / 10).toFixed(1)}%`;
}

export function structuralCoverageIsLow(ratio: number | null | undefined): boolean {
  return ratio != null && Number.isFinite(ratio) && ratio < STRUCTURAL_COVERAGE_TARGET;
}
