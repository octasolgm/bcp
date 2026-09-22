import { ChangeDetectorRef, Component, OnDestroy, OnInit, computed, effect, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import {
  NdPipelinePanelService,
  type PipelineDocRef,
  type PipelineExpansionMatch,
} from '../../services/nd/nd-pipeline-panel.service';
import { NdApiService, type NdOcrEngine } from '../../services/nd/nd-api.service';
import { sortByPointRef } from '../../../lib/nd/list-utils';

type ExpansionMatchKind = 'acronym' | 'synonym';

type StepKind = 'live' | 'placeholder';
type StepStatus = 'done' | 'running' | 'pending' | 'not_built';

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
    this.cdr.markForCheck();
    this.cdr.detectChanges();
  });

  readonly retrievalStatus = computed<StepStatus>(() => {
    const phase = (this.panel.phase() ?? '').toLowerCase();
    if (phase === 'retrieval') return 'running';
    if (this.panel.clauses().length > 0) return 'done';
    if (PHASES_PAST_RETRIEVAL.has(phase)) return 'done';
    return 'pending';
  });

  readonly processedCount = computed(() => this.panel.clauses().length);

  ngOnInit(): void {
    this.docPollTimer = setInterval(() => void this.refreshDocStatuses(this.panel.docs()), DOC_POLL_MS);
  }

  ngOnDestroy(): void {
    this.panelResizeCleanup?.();
    if (this.docPollTimer) clearInterval(this.docPollTimer);
  }

  statusFor(step: StepRow): StepStatus {
    if (step.kind === 'placeholder') return 'not_built';
    return this.retrievalStatus();
  }

  statusLabel(status: StepStatus): string {
    switch (status) {
      case 'done':
        return 'Done';
      case 'running':
        return 'Running…';
      case 'pending':
        return 'Pending';
      default:
        return 'Not built yet';
    }
  }

  readonly panelWidth = signal(NdPipelineProgressPanelComponent.loadPanelWidth());
  private static readonly MIN_WIDTH = 300;
  private static readonly MAX_WIDTH = 900;
  private panelResizeCleanup: (() => void) | null = null;

  private static loadPanelWidth(): number {
    try {
      const v = Number(localStorage.getItem('nd-pipeline-panel-width'));
      return v >= 300 && v <= 900 ? v : 380;
    } catch {
      return 380;
    }
  }

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
      try {
        localStorage.setItem('nd-pipeline-panel-width', String(this.panelWidth()));
      } catch {
        /* ignore */
      }
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

  toggleClause(clauseNo: string): void {
    this.expandedClauseNo = this.expandedClauseNo === clauseNo ? null : clauseNo;
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
