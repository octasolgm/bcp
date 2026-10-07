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
  console.groupCollapsed(
    `%c[Pipeline] Clause ${clauseNo} — Steps 1-6 (retrieval, pipeline v${r.pipelineVersion ?? 1}): ${r.fusedMatches?.length ?? 0} chunk(s) selected for the AI`,
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
  const calls = traces.filter((t) => t.step === 'llm_call' || t.step === 'evidence_check');
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
    } else if (t.step === 'llm_call' || t.step === 'evidence_check') {
      const label = t.step === 'evidence_check' ? 'Evidence check AI call (gap by gap)' : 'Step 8 — AI call';
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
