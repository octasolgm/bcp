import type { NdClauseTrace } from '../../app/services/nd/nd-api.service';

/* eslint-disable no-console */

const STYLE_HEAD = 'color:#1565c0;font-weight:bold';
const STYLE_STEP = 'color:#2e7d32;font-weight:bold';
const STYLE_WARN = 'color:#c62828;font-weight:bold';

const SOURCE_LABELS: Record<string, string> = {
  analysis: 'new analysis',
  rerun_all: 'rerun all',
  clause_rerun: 'clause rerun',
  gap_evidence: 'gap evidence re-check',
};

type AnyMatch = {
  sectionId?: string;
  clauseNo?: string | null;
  sourceDocumentName?: string | null;
  sourcePage?: number | null;
  textPreview?: string;
  score?: number;
  similarity?: number;
  fusedScore?: number;
  bm25Score?: number | null;
  embeddingSimilarity?: number | null;
  matchedSubObligation?: string | null;
  matchedVia?: string | null;
};

type Retrieval = {
  pipelineVersion?: number;
  expandedQueries?: string[];
  acronymMatches?: Array<{ matchedText?: string; addedText?: string }>;
  synonymMatches?: Array<{ matchedText?: string; addedText?: string }>;
  subObligations?: string[];
  bm25Matches?: AnyMatch[];
  matches?: AnyMatch[];
  fusedMatches?: AnyMatch[];
  elapsedMs?: number | null;
  embeddingModel?: string | null;
  institutionNames?: string[] | null;
};

function matchRows(list: AnyMatch[] | undefined, scoreKey: keyof AnyMatch) {
  return (list ?? []).map((m, i) => ({
    '#': i + 1,
    document: m.sourceDocumentName ?? '',
    section: m.clauseNo ?? '',
    page: m.sourcePage ?? '',
    score: m[scoreKey] ?? '',
    via: m.matchedSubObligation ?? '',
    wording: m.matchedVia === 'expanded' ? 'expanded (acronym/synonym swapped)' : 'clause',
    preview: (m.textPreview ?? '').slice(0, 160),
  }));
}

/** Steps 1-6 for one clause, as saved on the clause's retrieval record. */
export function logClauseRetrieval(clauseNo: string, raw: unknown): void {
  const r = (raw ?? {}) as Retrieval;
  reportRetrieval.set(clauseNo, r);
  showReportHint();
  console.groupCollapsed(
    `%c[Pipeline] Clause ${clauseNo} — Steps 1-6 (retrieval, pipeline v${r.pipelineVersion ?? 1}${r.elapsedMs != null ? `, ${r.elapsedMs} ms` : ''}): ${r.fusedMatches?.length ?? 0} chunk(s) selected for the AI`,
    STYLE_HEAD,
  );

  console.log('%cStep 1 — Query expansion', STYLE_STEP);
  console.table([
    ...(r.acronymMatches ?? []).map((m) => ({ type: 'acronym', matched: m.matchedText, added: m.addedText })),
    ...(r.synonymMatches ?? []).map((m) => ({ type: 'synonym', matched: m.matchedText, added: m.addedText })),
  ]);

  if ((r.pipelineVersion ?? 1) >= 2) {
    console.log(
      '%cStep 1 — Expanded wording also searched in Steps 3 and 4',
      STYLE_STEP,
      `${r.expandedQueries?.length ?? 0} query(ies)`,
    );
    (r.expandedQueries ?? []).forEach((q, i) => console.log(`  ${i + 1}.`, q));
  }

  console.log('%cStep 2 — Sub-obligation split', STYLE_STEP, `${r.subObligations?.length ?? 0} part(s)`);
  (r.subObligations ?? []).forEach((s, i) => console.log(`  ${i + 1}.`, s));

  console.log('%cStep 3 — BM25 keyword search', STYLE_STEP, `${r.bm25Matches?.length ?? 0} match(es)`);
  console.table(matchRows(r.bm25Matches, 'score'));

  console.log('%cStep 4 — Embedding search', STYLE_STEP, `${r.matches?.length ?? 0} match(es)`);
  console.table(matchRows(r.matches, 'similarity'));

  console.log(
    '%cStep 5+6 — Fusion and adaptive select (this is what Step 7 sends to the AI)',
    STYLE_STEP,
    `${r.fusedMatches?.length ?? 0} chunk(s)`,
  );
  console.table(matchRows(r.fusedMatches, 'fusedScore'));
  console.groupEnd();
}

/** Step 7 context, every Step 8 AI call (request + raw response) and the saved result for one clause. */
export function logClauseTraces(clauseNo: string, traces: NdClauseTrace[]): void {
  if (traces.length === 0) return;
  const known = reportTraces.get(clauseNo) ?? new Map<string, NdClauseTrace>();
  traces.forEach((t) => known.set(t.id, t));
  reportTraces.set(clauseNo, known);
  showReportHint();
  const calls = traces.filter((t) => t.step === 'llm_call' || t.step === 'evidence_check' || t.step === 'gap_verify');
  const failed = calls.some((t) => !!t.error);
  const sources = [...new Set(traces.map((t) => SOURCE_LABELS[t.source] ?? t.source))].join(', ');
  console.groupCollapsed(
    `%c[Pipeline] Clause ${clauseNo} — Step 7 (context) + Step 8 (AI judgment) [${sources}]: ${calls.length} AI call(s)${failed ? ' — ERROR' : ''}`,
    failed ? STYLE_WARN : STYLE_HEAD,
  );

  for (const t of traces) {
    const when = new Date(t.createdAt).toLocaleTimeString();
    if (t.step === 'context') {
      console.groupCollapsed(`%cStep 7 — Context sent to the AI (${t.charsSent ?? 0} chars) · ${when}`, STYLE_STEP);
      console.log(t.notes);
      try {
        console.table(JSON.parse(t.chunksJson ?? '[]'));
      } catch {
        /* chunk list is informational only */
      }
      console.log(t.contextText);
      if (t.clauseContext != null) {
        console.groupCollapsed(
          `%cSupporting regulatory context (parent, sibling and sub-clause headings) · ${t.clauseContextSent ? 'SENT in the request' : 'built, NOT sent (current prompt has no {clause_context})'}`,
          STYLE_STEP,
        );
        console.log(t.clauseContext);
        console.groupEnd();
      }
      console.groupEnd();
    } else if (t.step === 'llm_call' || t.step === 'evidence_check' || t.step === 'gap_verify') {
      const label =
        t.step === 'evidence_check'
          ? 'Evidence check AI call (gap by gap)'
          : t.step === 'gap_verify'
            ? 'Gap check AI call (pipeline v5)'
            : 'Step 8 — AI call';
      console.groupCollapsed(
        `%c${label}, attempt ${t.attempt} · ${t.provider}/${t.model} · ${t.durationMs ?? '?'} ms · ${when}${t.error ? ' · ERROR' : ''}`,
        t.error ? STYLE_WARN : STYLE_STEP,
      );
      console.log('Sent (chars):', t.charsSent, '· received (chars):', t.responseText?.length ?? 0);
      if (t.notes) console.log('Post-processing:', t.notes);
      if (t.error) console.error('Error:', t.error);
      if (t.systemPrompt) {
        console.groupCollapsed('System prompt');
        console.log(t.systemPrompt);
        console.groupEnd();
      }
      console.groupCollapsed(
        t.step === 'evidence_check'
          ? 'Request (full prompt incl. gaps, actions, re-analysis and evidence text)'
          : t.step === 'gap_verify'
            ? 'Request (the gap and every passage the wider search selected)'
            : 'Request (clause + instructions; the context is in Step 7)',
      );
      console.log(t.queryText);
      console.groupEnd();
      console.groupCollapsed('Raw AI response');
      console.log(t.responseText ?? '(no response)');
      console.groupEnd();
      console.groupEnd();
    } else {
      console.groupCollapsed(`%cSaved result — ${t.notes ?? ''} · ${when}`, STYLE_STEP);
      try {
        console.log(JSON.parse(t.resultJson ?? '{}'));
      } catch {
        console.log(t.resultJson);
      }
      console.groupEnd();
    }
  }
  console.groupEnd();
}

// ---------------------------------------------------------------------------------------------
// Copyable report: everything the console groups above show for a clause, as one plain text block,
// so it can be pasted into a chat or a ticket. In the browser console:
//   copy(bcpReport('3.5'))         compact: steps, selected passages, AI answers, gap checks, result
//   copy(bcpReport('3.5', true))   also the full context, system prompt and every request
//   copy(bcpReport())              every clause logged on this page
// ---------------------------------------------------------------------------------------------

const reportRetrieval = new Map<string, Retrieval>();
const reportTraces = new Map<string, Map<string, NdClauseTrace>>();
let reportHintShown = false;

const STEP_NAMES: Record<string, string> = {
  context: 'Step 7 - context sent to the AI',
  llm_call: 'Step 8 - AI judgment call',
  gap_verify: 'Gap check AI call (pipeline v5)',
  evidence_check: 'Evidence check AI call',
  postprocess: 'Saved result',
};

function showReportHint(): void {
  installReportCommand();
  if (reportHintShown) return;
  reportHintShown = true;
  console.log(
    "%c[Pipeline] Report for the chat: use 'Download report' in the pipeline panel, or type bcpDownload('3.5') here (saves a .txt file). copy(bcpReport('3.5')) copies it instead; add , true for the full prompts and context.",
    STYLE_HEAD,
  );
}

function matchLines(title: string, list: AnyMatch[] | undefined, scoreKey: keyof AnyMatch, full: boolean): string[] {
  const rows = list ?? [];
  const out = [`${title} (${rows.length}):`];
  rows.forEach((m, i) => {
    const score = m[scoreKey];
    const parts = [
      m.sourceDocumentName ?? 'document',
      m.clauseNo ? `section ${m.clauseNo}` : '',
      m.sourcePage != null ? `p.${m.sourcePage}` : '',
      score != null && score !== '' ? `score ${typeof score === 'number' ? score.toFixed(4) : score}` : '',
      m.matchedVia === 'expanded' ? 'via expanded wording' : '',
    ].filter((x) => x);
    out.push(`  ${i + 1}. ${parts.join(' | ')}`);
    if (m.matchedSubObligation) out.push(`     part: ${m.matchedSubObligation.slice(0, full ? 400 : 120)}`);
    const preview = (m.textPreview ?? '').replace(/\s+/g, ' ').trim();
    if (preview) out.push(`     text: ${full ? preview : preview.slice(0, 300)}`);
  });
  return out;
}

function retrievalLines(r: Retrieval, full: boolean): string[] {
  const out = [
    `--- Steps 1-6: retrieval (pipeline v${r.pipelineVersion ?? 1}${r.elapsedMs != null ? `, ${r.elapsedMs} ms` : ''}) ---`,
  ];
  if (r.embeddingModel) out.push(`Embedding model: ${r.embeddingModel}`);
  if (r.institutionNames?.length) out.push(`Bank's own names searched for "financial institution": ${r.institutionNames.join(', ')}`);
  const acr = (r.acronymMatches ?? []).map((m) => `${m.matchedText} -> ${m.addedText}`);
  const syn = (r.synonymMatches ?? []).map((m) => `${m.matchedText} -> ${m.addedText}`);
  out.push(`Step 1 acronyms (${acr.length}): ${acr.join('; ') || 'none'}`);
  out.push(`Step 1 synonyms (${syn.length}): ${syn.join('; ') || 'none'}`);
  out.push(`Step 1 expanded wording searched (${r.expandedQueries?.length ?? 0}):`);
  (r.expandedQueries ?? []).forEach((q, i) => out.push(`  ${i + 1}. ${q}`));
  out.push(`Step 2 parts searched (${r.subObligations?.length ?? 0}):`);
  (r.subObligations ?? []).forEach((q, i) => out.push(`  ${i + 1}. ${q}`));
  out.push(...matchLines('Step 3 keyword (BM25) matches', r.bm25Matches, 'score', full));
  out.push(...matchLines('Step 4 meaning (embedding) matches', r.matches, 'similarity', full));
  out.push(...matchLines('Step 5+6 SELECTED for the AI', r.fusedMatches, 'fusedScore', full));
  return out;
}

/** Gap check requests carry every passage; the compact report keeps the gap and the passage labels only. */
function gapCheckSummary(query: string): string[] {
  const gap = /GAP TO CHECK:\s*([\s\S]*?)\n\s*\n/.exec(query)?.[1]?.trim();
  const labels = [...query.matchAll(/^\[(E\d+)\]\s*(.+)$/gm)].map((m) => `  [${m[1]}] ${m[2]}`);
  return [`  Gap checked: ${gap ?? '(not found in request)'}`, `  Passages searched (${labels.length}):`, ...labels];
}

function traceLines(traces: NdClauseTrace[], full: boolean): string[] {
  const out: string[] = [];
  traces
    .slice()
    .sort((a, b) => a.createdAt.localeCompare(b.createdAt))
    .forEach((t, i) => {
      const when = new Date(t.createdAt).toISOString();
      const source = SOURCE_LABELS[t.source] ?? t.source;
      out.push('');
      out.push(`--- [${i + 1}] ${STEP_NAMES[t.step] ?? t.step} (${source}) ${when} ---`);
      if (t.step === 'context') {
        if (t.notes) out.push(`Notes: ${t.notes}`);
        try {
          const chunks = JSON.parse(t.chunksJson ?? '[]') as Array<{ label?: string; chars?: number }>;
          out.push(`Passages sent (${chunks.length}):`);
          chunks.forEach((c, n) => out.push(`  ${n + 1}. ${c.label ?? ''} (${c.chars ?? '?'} chars)`));
        } catch {
          /* chunk list is informational only */
        }
        if (full) {
          if (t.clauseContext) out.push('Supporting regulatory context:', t.clauseContext);
          out.push('Context text:', t.contextText ?? '');
        }
        return;
      }
      if (t.step === 'postprocess') {
        if (t.notes) out.push(`Notes: ${t.notes}`);
        try {
          out.push(JSON.stringify(JSON.parse(t.resultJson ?? '{}'), null, 2));
        } catch {
          out.push(t.resultJson ?? '');
        }
        return;
      }
      out.push(
        `Attempt ${t.attempt} | ${t.provider ?? '?'}/${t.model ?? '?'} | ${t.durationMs ?? '?'} ms | sent ${t.charsSent ?? '?'} chars | received ${t.responseText?.length ?? 0} chars`,
      );
      if (t.notes) out.push(`Post-processing: ${t.notes}`);
      if (t.error) out.push(`ERROR: ${t.error}`);
      if (full && t.systemPrompt) out.push('System prompt:', t.systemPrompt);
      if (full) out.push('Request:', t.queryText ?? '');
      else if (t.step === 'gap_verify') out.push(...gapCheckSummary(t.queryText ?? ''));
      out.push('AI response:', t.responseText ?? '(no response)');
    });
  return out;
}

type ReportSource = {
  retrieval: Map<string, Retrieval>;
  traces: Map<string, Map<string, NdClauseTrace>>;
};

/** Report data straight from the server (run status + clause traces), so the report never depends on what this
 * page happened to log (a reloaded page or a finished run logs nothing until it polls). */
export function reportSourceFromServer(
  retrievalPreview: Array<{ clauseNo: string; retrieval: unknown }>,
  traces: NdClauseTrace[],
): ReportSource {
  const retrieval = new Map<string, Retrieval>();
  retrievalPreview.forEach((p) => retrieval.set(p.clauseNo, (p.retrieval ?? {}) as Retrieval));
  const byClause = new Map<string, Map<string, NdClauseTrace>>();
  traces.forEach((t) => {
    const m = byClause.get(t.clauseNo) ?? new Map<string, NdClauseTrace>();
    m.set(t.id, t);
    byClause.set(t.clauseNo, m);
  });
  return { retrieval, traces: byClause };
}

/** Plain-text report for one clause (or every clause when clauseNo is omitted): from the server data when given,
 * else from what this page has logged. */
export function buildClauseReport(clauseNo?: string, full = false, source?: ReportSource): string {
  const retrievalMap = source?.retrieval ?? reportRetrieval;
  const tracesMap = source?.traces ?? reportTraces;
  const clauses = clauseNo
    ? [clauseNo]
    : [...new Set([...retrievalMap.keys(), ...tracesMap.keys()])].sort((a, b) =>
        a.localeCompare(b, undefined, { numeric: true }),
      );
  const out: string[] = [];
  if (clauses.length === 0) {
    out.push(
      `=== Pipeline report - ${new Date().toISOString()} ===`,
      'Nothing to report: no clause of this run has retrieval data or AI traces yet (or the run could not be loaded).',
      'Open the run on the V5 analysis page and wait until at least one clause has finished Steps 1-6.',
    );
  }
  for (const c of clauses) {
    const r = retrievalMap.get(c);
    const traces = [...(tracesMap.get(c)?.values() ?? [])];
    out.push(`=== Pipeline report: clause ${c} (${full ? 'full' : 'compact'}) - ${new Date().toISOString()} ===`);
    if (typeof location !== 'undefined') out.push(`Page: ${location.pathname}`);
    if (!r && traces.length === 0) {
      out.push('Nothing logged for this clause on this page yet. Open the clause result (or wait for the run) and try again.');
      continue;
    }
    if (r) out.push(...retrievalLines(r, full));
    else out.push('Steps 1-6: not loaded on this page.');
    if (traces.length) out.push(...traceLines(traces, full));
    else out.push('', 'Steps 7-8: no AI traces loaded yet.');
    out.push('', `=== End of report for clause ${c} ===`, '');
  }
  return out.join('\n');
}

/** True once anything has been logged for a clause on this page (the panel's download button uses it). */
export function hasClauseReport(): boolean {
  return reportRetrieval.size > 0 || reportTraces.size > 0;
}

/** Saves the report as a .txt file: no DevTools needed, and large reports do not slow the console down. */
export function downloadClauseReport(clauseNo?: string, full = false, source?: ReportSource): void {
  const text = buildClauseReport(clauseNo, full, source);
  const blob = new Blob([text], { type: 'text/plain;charset=utf-8' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  const stamp = new Date().toISOString().slice(0, 19).replace(/[:T]/g, '-');
  a.href = url;
  a.download = `pipeline-report-${clauseNo ? clauseNo.replace(/[^\w.-]+/g, '_') : 'all'}${full ? '-full' : ''}-${stamp}.txt`;
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

function installReportCommand(): void {
  const w = window as unknown as {
    bcpReport?: (clauseNo?: string, full?: boolean) => string;
    bcpDownload?: (clauseNo?: string, full?: boolean) => void;
  };
  if (w.bcpReport) return;
  w.bcpReport = (clauseNo?: string, full = false) => buildClauseReport(clauseNo, full);
  w.bcpDownload = (clauseNo?: string, full = false) => downloadClauseReport(clauseNo, full);
}
