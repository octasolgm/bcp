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

/** Router link segments for analysis runs opened inside ND. */
export function ndAnalysisRunLink(
  run: AnalysisRunSummary,
  role?: string | null,
): string[] {
  if (role === 'checker') return ['/nd/checker/review', run.id];
  if (role === 'reviewer') return ['/nd/reviewer/review', run.id];

  if (isAnalysisRunInProgress(run.status)) {
    return ['/nd/analyse-v8'];
  }

  if (!isLegacyAnalysisRun(run)) {
    return ['/nd/analyse-v8'];
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
  if (role === 'checker' || role === 'reviewer') return undefined;

  if (isAnalysisRunInProgress(run.status)) {
    if (!isLegacyAnalysisRun(run)) return { run: run.id };
    if (run.legacySessionId) return { session: run.legacySessionId };
    if (run.legacyHref) {
      const parsed = parseNdHref(run.legacyHref);
      if (parsed.queryParams?.['session']) return { session: parsed.queryParams['session'] };
    }
    return undefined;
  }

  if (!isLegacyAnalysisRun(run)) return { run: run.id };
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
