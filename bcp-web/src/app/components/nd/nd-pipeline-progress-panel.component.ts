import { ChangeDetectorRef, Component, ElementRef, OnDestroy, OnInit, computed, effect, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import {
  NdPipelinePanelService,
  type PipelineDocRef,
  type PipelineExpansionMatch,
} from '../../services/nd/nd-pipeline-panel.service';
import { NdApiService, type NdClauseTrace, type NdOcrEngine } from '../../services/nd/nd-api.service';
import { sortByPointRef } from '../../../lib/nd/list-utils';

type ExpansionMatchKind = 'acronym' | 'synonym';

function formatDuration(ms: number): string {
  const s = Math.max(0, Math.round(ms / 1000));
  const m = Math.floor(s / 60);
  const rem = s % 60;
  return m > 0 ? `${m}m ${rem.toString().padStart(2, '0')}s` : `${rem}s`;
}

type StepKind = 'live' | 'placeholder';
type StepStatus = 'done' | 'running' | 'pending' | 'error' | 'not_built' | 'not_started';

type StepRow = {
  key: string;
  label: string;
  kind: StepKind;
};

// Order matters here: Steps 1 through 6 all run in this engine today (see
// RegulEmbeddingRetrievalService / SubObligationSplitter / HybridFusionSelector) — listed by
// execution order.
const STEPS: StepRow[] = [
  { key: 'step1', label: 'Step 1 — Query expansion', kind: 'live' },
  { key: 'step2', label: 'Step 2 — Sub-obligation split', kind: 'live' },
  { key: 'step3', label: 'Step 3 — BM25 keyword search', kind: 'live' },
  { key: 'step4', label: 'Step 4 — Embedding / synonym retrieve', kind: 'live' },
  { key: 'step5', label: 'Step 5 — Fusion', kind: 'live' },
  { key: 'step6', label: 'Step 6 — Adaptive select', kind: 'live' },
  { key: 'step7', label: 'Step 7 — Build context', kind: 'live' },
  { key: 'step8', label: 'Step 8 — LLM judgment (retrieval-aware)', kind: 'live' },
  { key: 'step9', label: 'Step 9 — Save', kind: 'live' },
];

const PHASES_PAST_RETRIEVAL = new Set(['forward', 'reverse', 'qualitative', 'done']);
const DOC_POLL_MS = 5000;

type DocStatusRow = {
  id: string;
  name: string;
  parseStatus: string;
  extractStatus: string;
  indexStatus: string;
  /** Engine the best-progress row came from — actions re-use it so "Run" continues that same
   * document's pipeline instead of starting a fresh, unrelated tesseract one. */
  engine: NdOcrEngine;
};

/**
 * Right-side collapsible pipeline panel — V5 / hybrid-engine pages only, mounted via
 * NdShellComponent and gated by NdPipelinePanelService.active(). Two sections:
 *
 * - Documents: the run's selected internal documents with their real Parse/Extract/Index
 *   status per document (fetched from the same /nd/local-documents status endpoint the
 *   internal-documents page uses) — red-highlighted with a "Run" button when a step is
 *   missing for that document, so a document can be readied without leaving this page.
 * - Pipeline steps: Steps 1-7 are real and live once a run is active; Step 8's placeholder
 *   status reflects its LLM call being paused (see NdRegulAnalysisProcessor), and Step 9 shows
 *   as "not built" since there's no dedicated live indicator for it yet.
 */
@Component({
  selector: 'app-nd-pipeline-progress-panel',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './nd-pipeline-progress-panel.component.html',
  styleUrl: './nd-pipeline-progress-panel.component.scss',
})
export class NdPipelineProgressPanelComponent implements OnInit, OnDestroy {
  private readonly panel = inject(NdPipelinePanelService);
  private readonly ndApi = inject(NdApiService);
  private readonly cdr = inject(ChangeDetectorRef);
  private readonly host = inject(ElementRef<HTMLElement>);

  readonly steps = STEPS;
  readonly collapsed = this.panel.collapsed;
  // Clauses arrive in whatever order the backend processed them in (creation/selection order,
  // not clause order) — sort by clause number so §1.1 always reads before §2.5 in every step's
  // feed, matching how the regulation points list itself is ordered.
  readonly clauses = computed(() => sortByPointRef(this.panel.clauses(), (c) => c.clauseNo));
  expandedClauseNo: string | null = null;

  readonly docStatuses = signal<DocStatusRow[]>([]);
  readonly docsLoading = signal(false);
  actionBusyKey: string | null = null;

  private docPollTimer: ReturnType<typeof setInterval> | null = null;

  private readonly syncDocsOnChange = effect(() => {
    void this.refreshDocStatuses(this.panel.docs());
  });

  // Step 1-7's live status/labels are plain method calls (statusFor/statusLabel) reading
  // service-level signals rather than direct signal bindings in the template, so Angular's
  // template-level signal tracking doesn't pick them up on its own — confirmed live: the
  // underlying computed value (retrievalStatus) updates correctly the instant a run's
  // retrieval data lands, but the rendered "Pending" text stayed stale until something else
  // forced a change-detection pass on this component. This effect explicitly forces one
  // every time the clause data or phase actually changes, so the panel updates on its own
  // instead of only refreching when an unrelated click happens to trigger CD nearby.
  private readonly forceRenderOnDataChange = effect(() => {
    this.panel.clauses();
    this.panel.phase();
    this.panel.runActive();
    this.panel.judgmentFailures();
    this.panel.totalClauses();
    this.panel.judgedClauses();
    this.fullTexts();
    this.loadingSectionIds();
    this.tracesByClause();
    this.tracesLoading();
    this.tracesError();
    if (this.judgmentStatus() === 'running') this.now();
    this.cdr.markForCheck();
    this.cdr.detectChanges();
  });

  /** Steps 1-7 — the retrieval pipeline (query expansion through build-context) — all finish
   * together, at the same point the backend hands off from retrieval to judgment. */
  readonly retrievalStatus = computed<StepStatus>(() => {
    if (!this.panel.runActive()) return 'not_started';
    const phase = (this.panel.phase() ?? '').toLowerCase();
    if (phase === 'retrieval' || phase === 'passages') return 'running';
    if (this.panel.clauses().length > 0) return 'done';
    if (PHASES_PAST_RETRIEVAL.has(phase)) return 'done';
    return 'pending';
  });

  /** The step a running analysis is on: Step 1 from the start of the run until retrieval is done, then Step 8.
   * The panel scrolls to it each time it changes, so the step in progress is always in view. */
  readonly currentStepKey = computed<string | null>(() => {
    if (!this.panel.runActive()) return null;
    const phase = (this.panel.phase() ?? '').toLowerCase();
    if (['queued', 'parsing', 'passages', 'retrieval'].includes(phase)) return 'step1';
    if (this.judgmentStatus() === 'running') return 'step8';
    return null;
  });

  private lastScrolledStep: string | null = null;
  private readonly followCurrentStep = effect(() => {
    const key = this.currentStepKey();
    const runId = this.panel.runId();
    const marker = key ? `${runId}:${key}` : null;
    if (!marker || marker === this.lastScrolledStep) return;
    this.lastScrolledStep = marker;
    // After this change-detection pass has rendered the row.
    setTimeout(() => {
      const row = (this.host.nativeElement as HTMLElement).querySelector<HTMLElement>(`[data-step="${key}"]`);
      row?.scrollIntoView({ block: key === 'step1' ? 'start' : 'nearest', behavior: 'smooth' });
    }, 0);
  });

  /** Pipeline v4+: the run is building search passages for documents indexed before passages existed. */
  readonly preparingPassages = computed(
    () => this.panel.runActive() && (this.panel.phase() ?? '').toLowerCase() === 'passages',
  );

  /** Step 8 — LLM judgment — only starts once retrieval has actually finished. */
  readonly judgmentStatus = computed<StepStatus>(() => {
    if (!this.panel.runActive()) return 'not_started';
    const phase = (this.panel.phase() ?? '').toLowerCase();
    if (phase === 'done') {
      return this.panel.judgmentFailures() > 0 ? 'error' : 'done';
    }
    if (PHASES_PAST_RETRIEVAL.has(phase)) return 'running';
    return 'pending';
  });

  /** Step 9 — Save — only done once the whole run reaches its final phase. */
  readonly saveStatus = computed<StepStatus>(() => {
    if (!this.panel.runActive()) return 'not_started';
    const phase = (this.panel.phase() ?? '').toLowerCase();
    if (phase === 'done') return 'done';
    if (PHASES_PAST_RETRIEVAL.has(phase)) return 'pending';
    return 'pending';
  });

  readonly processedCount = computed(() => this.panel.clauses().length);

  /** Ticks every second so elapsed / remaining time on Step 8 stays live. */
  private readonly now = signal(Date.now());
  private clockTimer: ReturnType<typeof setInterval> | null = null;
  private judgmentStartedAt: number | null = null;
  private judgmentStartedRunId: string | null = null;

  private readonly trackJudgmentStart = effect(() => {
    const status = this.judgmentStatus();
    const runId = this.panel.runId();
    if (runId !== this.judgmentStartedRunId) {
      this.judgmentStartedRunId = runId;
      this.judgmentStartedAt = null;
    }
    if (status === 'running' && this.judgmentStartedAt == null) this.judgmentStartedAt = Date.now();
  });

  progressFor(step: StepRow): { done: number; total: number; percent: number; timing: string } | null {
    if (!this.panel.runActive()) return null;
    const total = this.panel.totalClauses();
    if (total <= 0) return null;

    const isJudgment = step.key === 'step8' || step.key === 'step9';
    const status = this.statusFor(step);
    let done: number;
    if (isJudgment) {
      done = status === 'done' || status === 'error' ? total : this.panel.judgedClauses();
    } else {
      done = status === 'done' ? total : this.panel.clauses().length;
    }
    done = Math.min(total, Math.max(0, done));
    const percent = Math.round((done / total) * 100);

    let timing = '';
    if (step.key === 'step8' && status === 'running' && this.judgmentStartedAt != null) {
      const elapsedMs = this.now() - this.judgmentStartedAt;
      timing = `elapsed ${formatDuration(elapsedMs)}`;
      if (done > 0 && done < total) {
        timing += ` · ~${formatDuration((elapsedMs / done) * (total - done))} left`;
      }
    }
    return { done, total, percent, timing };
  }

  /** Full section text, fetched on demand when a clause is expanded (the poll only carries a preview). */
  private readonly fullTexts = signal<Record<string, string>>({});
  private readonly loadingSectionIds = signal<ReadonlySet<string>>(new Set());

  fullTextFor(match: { sectionId: string; textPreview: string }): string {
    return this.fullTexts()[match.sectionId] ?? match.textPreview;
  }

  isLoadingFullText(match: { sectionId: string }): boolean {
    return this.loadingSectionIds().has(match.sectionId);
  }

  private async loadFullTextsForClause(clauseNo: string): Promise<void> {
    const runId = this.panel.runId();
    const clause = this.panel.clauses().find((c) => c.clauseNo === clauseNo);
    if (!runId || !clause) return;

    const have = this.fullTexts();
    const loading = this.loadingSectionIds();
    const ids = [
      ...new Set(
        [...clause.bm25Matches, ...clause.matches, ...clause.fusedMatches]
          .map((m) => m.sectionId)
          .filter((id) => id && !(id in have) && !loading.has(id)),
      ),
    ];
    if (ids.length === 0) return;

    this.loadingSectionIds.set(new Set([...loading, ...ids]));
    try {
      const res = await this.ndApi.getRetrievalSectionTexts(runId, ids);
      if (res.success && res.data) this.fullTexts.update((cur) => ({ ...cur, ...res.data }));
    } finally {
      this.loadingSectionIds.update((cur) => {
        const next = new Set(cur);
        ids.forEach((id) => next.delete(id));
        return next;
      });
      this.cdr.markForCheck();
    }
  }

  ngOnInit(): void {
    this.docPollTimer = setInterval(() => void this.refreshDocStatuses(this.panel.docs()), DOC_POLL_MS);
    this.clockTimer = setInterval(() => this.now.set(Date.now()), 1000);
  }

  ngOnDestroy(): void {
    this.panelResizeCleanup?.();
    if (this.docPollTimer) clearInterval(this.docPollTimer);
    if (this.clockTimer) clearInterval(this.clockTimer);
  }

  statusFor(step: StepRow): StepStatus {
    if (step.kind === 'placeholder') return 'not_built';
    if (step.key === 'step8') return this.judgmentStatus();
    if (step.key === 'step9') return this.saveStatus();
    return this.retrievalStatus();
  }

  statusLabel(status: StepStatus, step?: StepRow): string {
    if (status === 'error' && step?.key === 'step8') {
      const n = this.panel.judgmentFailures();
      return n === 1 ? '1 clause failed — retry' : `${n} clauses failed — retry`;
    }
    switch (status) {
      case 'done':
        return 'Done';
      case 'running':
        return 'Processing…';
      case 'pending':
        return 'Queued';
      case 'error':
        return 'Failed';
      case 'not_started':
        return '';
      default:
        return 'Not built yet';
    }
  }

  // Session-only — not persisted, so a refresh always starts at the default width.
  readonly panelWidth = signal(380);
  private static readonly MIN_WIDTH = 300;
  private static readonly MAX_WIDTH = 900;
  private panelResizeCleanup: (() => void) | null = null;

  /** Panel sits at the right edge, so dragging its left edge leftwards widens it. */
  startPanelResize(event: PointerEvent): void {
    event.preventDefault();
    this.panelResizeCleanup?.();
    const startX = event.clientX;
    const startWidth = this.panelWidth();
    const move = (e: PointerEvent) => {
      const next = startWidth + (startX - e.clientX);
      this.panelWidth.set(
        Math.round(Math.min(NdPipelineProgressPanelComponent.MAX_WIDTH, Math.max(NdPipelineProgressPanelComponent.MIN_WIDTH, next))),
      );
    };
    const up = () => {
      this.panelResizeCleanup?.();
    };
    document.addEventListener('pointermove', move);
    document.addEventListener('pointerup', up);
    this.panelResizeCleanup = () => {
      document.removeEventListener('pointermove', move);
      document.removeEventListener('pointerup', up);
      this.panelResizeCleanup = null;
    };
  }

  /** Steps finished, for the header summary. */
  readonly doneStepCount = computed(
    () => this.steps.filter((s) => s.kind === 'live' && this.statusFor(s) === 'done').length,
  );

  toggleCollapsed(): void {
    this.panel.toggleCollapsed();
  }

  toggleClause(key: string): void {
    this.expandedClauseNo = this.expandedClauseNo === key ? null : key;
    if (this.expandedClauseNo) {
      // Keys are "<clauseNo>" or "<clauseNo>:<view>[:<step>]".
      const clauseNo = this.panel.clauses().find((c) => key === c.clauseNo || key.startsWith(c.clauseNo + ':'))?.clauseNo;
      if (!clauseNo) return;
      if (key.endsWith(':ctx') || key.endsWith(':llm')) void this.loadTraces(clauseNo);
      else void this.loadFullTextsForClause(clauseNo);
    }
  }

  // ---- Step 7 / Step 8 audit trail (what the LLM was sent and returned), platform admin only.
  private readonly tracesByClause = signal<Record<string, NdClauseTrace[]>>({});
  readonly tracesLoading = signal<string | null>(null);
  readonly tracesError = signal<string | null>(null);
  /** Which raw text block is expanded: "<traceId>:<field>". */
  openTraceBlock: string | null = null;

  tracesFor(clauseNo: string): NdClauseTrace[] {
    return this.tracesByClause()[clauseNo] ?? [];
  }

  contextTraceFor(clauseNo: string): NdClauseTrace | null {
    const rows = this.tracesFor(clauseNo).filter((t) => t.step === 'context');
    return rows.length ? rows[rows.length - 1] : null;
  }

  /** Calls and final result of the latest judgment pass (a rerun starts a new pass with a new context row). */
  latestPassFor(clauseNo: string): NdClauseTrace[] {
    const rows = this.tracesFor(clauseNo);
    let start = 0;
    rows.forEach((t, i) => {
      if (t.step === 'context') start = i;
    });
    return rows.slice(start).filter((t) => t.step !== 'context');
  }

  chunkListFor(trace: NdClauseTrace | null): Array<{ label: string; chars: number }> {
    if (!trace?.chunksJson) return [];
    try {
      return JSON.parse(trace.chunksJson) as Array<{ label: string; chars: number }>;
    } catch {
      return [];
    }
  }

  toggleTraceBlock(key: string): void {
    this.openTraceBlock = this.openTraceBlock === key ? null : key;
  }

  formatMs(ms: number | null): string {
    return ms == null ? '' : formatDuration(ms);
  }

  private async loadTraces(clauseNo: string): Promise<void> {
    const runId = this.panel.runId();
    if (!runId) return;
    this.tracesLoading.set(clauseNo);
    this.tracesError.set(null);
    try {
      const res = await this.ndApi.getClauseTraces(runId, clauseNo);
      if (res.success && res.data) {
        const rows = res.data;
        this.tracesByClause.update((cur) => ({ ...cur, [clauseNo]: rows }));
      } else {
        this.tracesError.set(res.message || 'Could not load the AI call log.');
      }
    } finally {
      this.tracesLoading.set(null);
      this.cdr.markForCheck();
    }
  }

  matchPercent(similarity: number): number {
    return Math.round(Math.max(0, Math.min(1, similarity)) * 100);
  }

  editingEntryId: string | null = null;
  editDraftA = '';
  editDraftB = '';
  savingEntryId: string | null = null;

  startEditMatch(match: PipelineExpansionMatch): void {
    this.editingEntryId = match.entryId;
    this.editDraftA = match.termA;
    this.editDraftB = match.termB;
  }

  cancelEditMatch(): void {
    this.editingEntryId = null;
  }

  async saveEditMatch(kind: ExpansionMatchKind): Promise<void> {
    const entryId = this.editingEntryId;
    if (!entryId) return;
    const termA = this.editDraftA.trim();
    const termB = this.editDraftB.trim();
    if (!termA || !termB) return;

    this.savingEntryId = entryId;
    try {
      const res =
        kind === 'acronym'
          ? await this.ndApi.updateDictionaryEntry(entryId, { acronym: termA, definition: termB })
          : await this.ndApi.updateSynonymEntry(entryId, { termA, termB });
      if (res.success) {
        this.panel.patchExpansionMatch(entryId, termA, termB);
        this.editingEntryId = null;
      }
    } finally {
      if (this.savingEntryId === entryId) this.savingEntryId = null;
    }
  }

  isParseDone(doc: DocStatusRow): boolean {
    return doc.parseStatus === 'parsed';
  }

  isExtractDone(doc: DocStatusRow): boolean {
    return doc.extractStatus === 'extracted';
  }

  isIndexDone(doc: DocStatusRow): boolean {
    return doc.indexStatus === 'indexed';
  }

  async runParse(doc: DocStatusRow): Promise<void> {
    const key = `${doc.id}:parse`;
    this.actionBusyKey = key;
    try {
      await this.ndApi.localParseById(doc.id, doc.engine);
    } finally {
      if (this.actionBusyKey === key) this.actionBusyKey = null;
      await this.refreshDocStatuses(this.panel.docs());
    }
  }

  async runExtract(doc: DocStatusRow): Promise<void> {
    const key = `${doc.id}:extract`;
    this.actionBusyKey = key;
    try {
      await this.ndApi.localExtractById(doc.id, doc.engine);
    } finally {
      if (this.actionBusyKey === key) this.actionBusyKey = null;
      await this.refreshDocStatuses(this.panel.docs());
    }
  }

  async runIndex(doc: DocStatusRow): Promise<void> {
    const key = `${doc.id}:index`;
    this.actionBusyKey = key;
    try {
      await this.ndApi.localReindexById(doc.id, doc.engine);
    } finally {
      if (this.actionBusyKey === key) this.actionBusyKey = null;
      await this.refreshDocStatuses(this.panel.docs());
    }
  }

  private async refreshDocStatuses(docs: readonly PipelineDocRef[]): Promise<void> {
    if (docs.length === 0) {
      this.docStatuses.set([]);
      return;
    }
    this.docsLoading.set(true);
    try {
      const res = await this.ndApi.localExtractStatusBatchAnyEngine(docs.map((d) => d.id), { lite: true });
      const byId = res.success ? res.data ?? {} : {};
      this.docStatuses.set(
        docs.map((d) => {
          const s = byId[d.id];
          return {
            id: d.id,
            name: d.name,
            parseStatus: s?.status ?? 'pending',
            extractStatus: s?.extractStatus ?? 'pending',
            indexStatus: s?.indexStatus ?? 'pending',
            engine: s?.engine ?? 'tesseract',
          };
        }),
      );
    } finally {
      this.docsLoading.set(false);
    }
  }
}
