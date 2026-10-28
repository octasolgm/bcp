import { Injectable, signal } from '@angular/core';

export type PipelineRetrievalMatch = {
  sectionId: string;
  clauseNo: string | null;
  textPreview: string;
  sourceDocumentId: string;
  sourceDocumentName: string | null;
  sourcePage: number | null;
  similarity: number;
  /** Step 2 — which sub-obligation of the clause this match came from, when the clause was split
   * into more than one; null when the clause wasn't split (matched as a single whole clause). */
  matchedSubObligation?: string | null;
  /** Pipeline v2 — "expanded" when the best score came from the expanded-wording search. */
  matchedVia?: string | null;
};

export type PipelineBm25Match = {
  sectionId: string;
  clauseNo: string | null;
  textPreview: string;
  sourceDocumentId: string;
  sourceDocumentName: string | null;
  sourcePage: number | null;
  score: number;
  matchedSubObligation?: string | null;
  matchedVia?: string | null;
};

export type PipelineFusedMatch = {
  sectionId: string;
  clauseNo: string | null;
  textPreview: string;
  sourceDocumentId: string;
  sourceDocumentName: string | null;
  sourcePage: number | null;
  fusedScore: number;
  bm25Score: number | null;
  embeddingSimilarity: number | null;
  matchedSubObligation?: string | null;
};

export type PipelineExpansionMatch = {
  entryId: string;
  /** Canonical stored pair, always in this fixed order (acronym/definition, or termA/termB) —
   * edit against these, not matchedText/addedText which vary by which side matched. */
  termA: string;
  termB: string;
  /** Which direction actually fired for this clause, for display only. */
  matchedText: string;
  addedText: string;
};

function isResolvedMatch(m: PipelineExpansionMatch): boolean {
  return !!m.termA?.trim() && !!m.termB?.trim();
}

export type PipelineClauseRetrieval = {
  clauseNo: string;
  acronymMatches: PipelineExpansionMatch[];
  synonymMatches: PipelineExpansionMatch[];
  bm25Matches: PipelineBm25Match[];
  matches: PipelineRetrievalMatch[];
  /** Step 2 — the sub-obligations this clause was split into before retrieval. A single entry
   * (the full clause text) means the splitter found no confident evidence of more than one
   * obligation. */
  subObligations: string[];
  /** Step 5+6 — the fused, ranked, trimmed list Step 7 actually builds context from. */
  fusedMatches: PipelineFusedMatch[];
  /** Retrieval pipeline version that produced this record (records from before versions existed are v1). */
  pipelineVersion: number;
  /** Pipeline v2 — the clause wording with acronyms/synonyms swapped, also searched in Steps 3 and 4. */
  expandedQueries: string[];
};

export type PipelineDocRef = {
  id: string;
  name: string;
};

/**
 * Backing store for the right-side pipeline progress panel (V5 / hybrid engine only) —
 * see NdPipelineProgressPanelComponent. Mirrors the NdShellFocusService pattern: the page
 * component (AnalyseRegulFullV2Component) pushes run state in via activate()/setPhase()/
 * setRetrievalPreview(), and the shell reads it reactively to render the panel without owning
 * any run state itself.
 */
@Injectable({ providedIn: 'root' })
export class NdPipelinePanelService {
  private readonly _active = signal(false);
  // Not persisted — a refresh always starts collapsed/open at the default state.
  private readonly _collapsed = signal(false);
  private readonly _phase = signal<string | null>(null);
  private readonly _runActive = signal(false);
  private readonly _clauses = signal<PipelineClauseRetrieval[]>([]);
  private readonly _docs = signal<PipelineDocRef[]>([]);
  /** Clauses whose forward LLM judgment failed — Step 8 stays in an error state until retried. */
  private readonly _judgmentFailures = signal(0);
  private readonly _runId = signal<string | null>(null);
  private readonly _totalClauses = signal(0);
  private readonly _judgedClauses = signal(0);

  readonly active = this._active.asReadonly();
  readonly collapsed = this._collapsed.asReadonly();
  readonly phase = this._phase.asReadonly();
  /** Whether an analysis run actually exists yet — distinct from `phase`, which stays null for a
   * moment right after the run is created but before the first phase update lands. Lets the panel
   * tell "nothing has started" apart from "started, first phase not reported yet". */
  readonly runActive = this._runActive.asReadonly();
  readonly clauses = this._clauses.asReadonly();
  readonly docs = this._docs.asReadonly();
  readonly judgmentFailures = this._judgmentFailures.asReadonly();
  readonly runId = this._runId.asReadonly();
  /** Clauses selected in the run, and how many have finished Step 8 (completed or failed). */
  readonly totalClauses = this._totalClauses.asReadonly();
  readonly judgedClauses = this._judgedClauses.asReadonly();

  setRunId(id: string | null): void {
    if (id !== this._runId()) this._runId.set(id);
  }

  setClauseProgress(total: number, judged: number): void {
    if (total !== this._totalClauses()) this._totalClauses.set(Math.max(0, total));
    if (judged !== this._judgedClauses()) this._judgedClauses.set(Math.max(0, judged));
  }

  /** Called by AnalyseRegulFullV2Component on init — makes the shell show the panel toggle. */
  activate(): void {
    this._active.set(true);
  }

  /** Called on destroy so the panel disappears when navigating away from V5. */
  deactivate(): void {
    this._active.set(false);
    this._phase.set(null);
    this._runActive.set(false);
    this._clauses.set([]);
    this._docs.set([]);
    this._judgmentFailures.set(0);
    this._runId.set(null);
    this._totalClauses.set(0);
    this._judgedClauses.set(0);
  }

  /** A new run is shown: forget the previous run's per-clause Steps 1-6 data and counters. */
  clearRunData(): void {
    this._clauses.set([]);
    this._judgmentFailures.set(0);
    this._totalClauses.set(0);
    this._judgedClauses.set(0);
  }

  setJudgmentFailures(count: number): void {
    const n = Math.max(0, count);
    if (n !== this._judgmentFailures()) this._judgmentFailures.set(n);
  }

  setPhase(phase: string | null): void {
    this._phase.set(phase);
  }

  setRunActive(active: boolean): void {
    this._runActive.set(active);
  }

  /** Selected internal documents for the current run/setup — id+name only; the panel fetches
   * and owns Parse/Extract/Index status itself via NdApiService. */
  setDocs(docs: PipelineDocRef[]): void {
    const changed =
      docs.length !== this._docs().length ||
      docs.some((d, i) => d.id !== this._docs()[i]?.id || d.name !== this._docs()[i]?.name);
    if (changed) this._docs.set(docs);
  }

  setRetrievalPreview(entries: Array<{ clauseNo: string; retrieval: unknown }>): void {
    const parsed: PipelineClauseRetrieval[] = entries.map((e) => {
      const r = (e.retrieval ?? {}) as {
        acronymMatches?: unknown;
        synonymMatches?: unknown;
        bm25Matches?: unknown;
        matches?: unknown;
        subObligations?: unknown;
        fusedMatches?: unknown;
        pipelineVersion?: unknown;
        expandedQueries?: unknown;
      };
      return {
        clauseNo: e.clauseNo,
        // Resolved pairs only (both sides filled in). Runs saved before the backend started
        // skipping unresolved dictionary entries still carry them in RetrievalJson — they expand
        // to nothing, so never show them.
        acronymMatches: Array.isArray(r.acronymMatches)
          ? (r.acronymMatches as PipelineExpansionMatch[]).filter(isResolvedMatch)
          : [],
        synonymMatches: Array.isArray(r.synonymMatches)
          ? (r.synonymMatches as PipelineExpansionMatch[]).filter(isResolvedMatch)
          : [],
        bm25Matches: Array.isArray(r.bm25Matches) ? (r.bm25Matches as PipelineBm25Match[]) : [],
        matches: Array.isArray(r.matches) ? (r.matches as PipelineRetrievalMatch[]) : [],
        subObligations: Array.isArray(r.subObligations) ? (r.subObligations as string[]) : [],
        fusedMatches: Array.isArray(r.fusedMatches) ? (r.fusedMatches as PipelineFusedMatch[]) : [],
        pipelineVersion: typeof r.pipelineVersion === 'number' ? r.pipelineVersion : 1,
        expandedQueries: Array.isArray(r.expandedQueries) ? (r.expandedQueries as string[]) : [],
      };
    });
    this._clauses.set(parsed);
  }

  /** Called by the panel after a successful inline edit of a matched dictionary/synonym row —
   * updates the term pair in place across every clause's cached match list, so the panel
   * reflects the edit immediately without waiting for the next poll tick. */
  patchExpansionMatch(entryId: string, termA: string, termB: string): void {
    this._clauses.update((clauses) =>
      clauses.map((c) => ({
        ...c,
        acronymMatches: c.acronymMatches.map((m) => (m.entryId === entryId ? { ...m, termA, termB } : m)),
        synonymMatches: c.synonymMatches.map((m) => (m.entryId === entryId ? { ...m, termA, termB } : m)),
      })),
    );
  }

  toggleCollapsed(): void {
    this._collapsed.update((v) => !v);
  }
}
