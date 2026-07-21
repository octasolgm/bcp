import type { AnalysisRunSummary } from './types';

export type NdRouteTarget = {
  routerLink: string[];
  queryParams?: Record<string, string>;
};

function analyseV8SessionQuery(sessionId: string): NdRouteTarget {
  return {
    routerLink: ['/nd/analyse-v8'],
    queryParams: { session: sessionId },
  };
}

/** Normalize legacy `/old/*` hrefs to ND routes; rewrite dual-verify links to analyse-v8. */
export function parseNdHref(href?: string | null): NdRouteTarget {
  let normalized = (href ?? '').replace(/^\/old\//, '/nd/');
  normalized = normalized.replace(
    /^\/nd\/regulations(?:\/([^?#/]+))?/,
    (_, id) => (id ? `/nd/regulation-documents/${id}` : '/nd/regulation-documents'),
  );
  if (!normalized || normalized === '/') {
    return { routerLink: ['/nd/analysis-runs'] };
  }

  const qIdx = normalized.indexOf('?');
  const path = qIdx >= 0 ? normalized.slice(0, qIdx) : normalized;
  const qs = qIdx >= 0 ? normalized.slice(qIdx + 1) : '';
  const queryParams: Record<string, string> = {};
  if (qs) {
    new URLSearchParams(qs).forEach((value, key) => {
      queryParams[key] = value;
    });
  }

  if (path.includes('/dual-verify')) {
    return queryParams['session']
      ? analyseV8SessionQuery(queryParams['session'])
      : { routerLink: ['/nd/analyse-v8'] };
  }

  return {
    routerLink: [path],
    ...(Object.keys(queryParams).length ? { queryParams } : {}),
  };
}

/** Draft / running / queued runs open analyse-v8 to resume; finished runs open gap analysis. */
export function isAnalysisRunInProgress(status: string): boolean {
  const s = status.toLowerCase();
  return (
    s === 'draft' ||
    s === 'running' ||
    s === 'processing' ||
    s === 'queued' ||
    s === 'pending'
  );
}

/** Analysis finished — maker can review gaps and submit for checker. */
export function isAnalysisRunAwaitingReview(status: string): boolean {
  const s = status.toLowerCase();
  return (
    s === 'completed' ||
    s === 'dual_verify_failed' ||
    s === 'landing_ai_complete' ||
    s === 'pulled_back' ||
    s === 'submitted_for_review' ||
    s === 'checker_approved' ||
    s === 'reviewer_approved'
  );
}

/**
 * Run still has pending points — open analyse-v8 (execution view + rerun),
 * not gap-analysis working document. A run with all points processed opens
 * the gap analysis even if dual verify flagged failures (maker reviews there).
 */
export function analysisRunNeedsExecutionView(run: AnalysisRunSummary): boolean {
  const s = (run.status ?? '').toLowerCase();
  if (['submitted_for_review', 'checker_approved', 'reviewer_approved', 'pulled_back'].includes(s)) {
    return false;
  }
  if (isAnalysisRunInProgress(s)) return true;

  const total = run.totalPointsCount ?? 0;
  const processed = run.processedPointsCount ?? 0;
  if (total > 0 && processed < total) return true;

  if (s === 'failed') return true;

  return false;
}

/** Router link segments for analysis runs opened inside ND. */
export function ndAnalysisRunLink(
  run: AnalysisRunSummary,
  role?: string | null,
): string[] {
  if (role === 'checker') {
    if (run.status === 'submitted_for_review') return ['/nd/checker/review', run.id];
    return ['/nd/gap-analysis'];
  }
  if (role === 'reviewer') {
    if (run.status === 'checker_approved') return ['/nd/reviewer/review', run.id];
    return ['/nd/gap-analysis'];
  }

  if (analysisRunNeedsExecutionView(run)) {
    return ['/nd/analyse-v8'];
  }

  if (!isLegacyAnalysisRun(run)) {
    return ['/nd/gap-analysis'];
  }

  if (isLegacyAnalysisRun(run) && run.legacyHref) {
    const parsed = parseNdHref(run.legacyHref);
    if (parsed.routerLink[0]?.includes('/gap-analysis')) {
      return parsed.routerLink;
    }
  }

  return ['/nd/gap-analysis'];
}

export function ndAnalysisRunQuery(
  run: AnalysisRunSummary,
  role?: string | null,
): Record<string, string> | undefined {
  if (role === 'checker') {
    if (run.status === 'submitted_for_review') return undefined;
    return { run: run.id };
  }
  if (role === 'reviewer') {
    if (run.status === 'checker_approved') return undefined;
    return { run: run.id };
  }

  if (!isLegacyAnalysisRun(run)) {
    if (analysisRunNeedsExecutionView(run)) {
      return { run: run.id };
    }
    return { run: run.id };
  }

  if (analysisRunNeedsExecutionView(run)) {
    if (run.legacySessionId) return { session: run.legacySessionId };
    if (run.legacyHref) {
      const parsed = parseNdHref(run.legacyHref);
      if (parsed.queryParams?.['session']) return { session: parsed.queryParams['session'] };
    }
  }

  if (run.legacySessionId) return { session: run.legacySessionId };
  if (run.legacyHref) return parseNdHref(run.legacyHref).queryParams;
  return undefined;
}

export function ndAnalysisRunTarget(
  run: AnalysisRunSummary,
  role?: string | null,
): NdRouteTarget {
  return {
    routerLink: ndAnalysisRunLink(run, role),
    queryParams: ndAnalysisRunQuery(run, role),
  };
}

export function isLegacyAnalysisRun(run: AnalysisRunSummary): boolean {
  return (
    run.source === 'legacy_analysis' ||
    run.source === 'legacy_dual_verify' ||
    !!run.legacyHref
  );
}

/** Always open regulation documents inside ND (never `/old/regulations`). */
export function ndRegulationDocumentLink(doc: {
  id: string;
  legacyHref?: string | null;
}): NdRouteTarget {
  if (doc.legacyHref) {
    const target = parseNdHref(doc.legacyHref);
    if (!target.routerLink[0]?.includes('/regulation-documents')) {
      return { routerLink: ['/nd/regulation-documents', doc.id] };
    }
    return target;
  }
  return { routerLink: ['/nd/regulation-documents', doc.id] };
}
