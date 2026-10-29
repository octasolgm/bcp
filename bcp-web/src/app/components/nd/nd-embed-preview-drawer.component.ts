import {
  ChangeDetectionStrategy,
  ChangeDetectorRef,
  Component,
  EventEmitter,
  HostListener,
  Input,
  OnChanges,
  Output,
  inject,
} from '@angular/core';
import { NdApiService } from '../../services/nd/nd-api.service';
import type { InternalDocumentFinalizeEmbed } from '../../../lib/nd/types';

type PreviewDoc = { id: string; title: string; version: string; embeds: InternalDocumentFinalizeEmbed[] };

/**
 * Right-side panel for a run's embedding. Before finalize: where each resolved action will be embedded -
 * internal document, page and clause - from the same placement finalize uses (the policy wording is only
 * written, by AI, at finalize). After finalize: the finalized documents with the text written into each,
 * and a download for each document.
 */
@Component({
  selector: 'app-nd-embed-preview-drawer',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="ep-backdrop" (click)="closed.emit()"></div>
    <aside class="ep-drawer" role="dialog" aria-modal="true" aria-labelledby="ep-title">
      <header class="ep-head">
        <div>
          <h2 id="ep-title">{{ finalized ? 'Embedded documents' : 'Embedding preview' }}</h2>
          <p class="ep-sub">
            {{ finalized
              ? 'The resolved actions written into each internal document when this report was finalized.'
              : 'Where each resolved action will be written when this report is finalized.' }}
          </p>
        </div>
        <div class="ep-head-actions">
          <button type="button" class="ep-link" [disabled]="loading" (click)="load()">
            {{ loading ? 'Refreshing...' : 'Refresh' }}
          </button>
          <button type="button" class="ep-close" aria-label="Close" (click)="closed.emit()">✕</button>
        </div>
      </header>

      <div class="ep-body">
        @if (loading && !docs.length) {
          <p class="ep-muted">Loading...</p>
        } @else if (error) {
          <p class="ep-error">{{ error }}</p>
        } @else if (!docs.length) {
          <p class="ep-muted">
            @if (finalized) {
              No documents were updated when this report was finalized.
            } @else {
              No resolved actions yet. Resolve actions in the report and they will appear here with the document and
              page they will be written on.
            }
          </p>
        } @else {
          <p class="ep-summary">{{ total }} action{{ total === 1 ? '' : 's' }} across {{ docs.length }} document{{ docs.length === 1 ? '' : 's' }}</p>
          @for (doc of docs; track doc.id) {
            <section class="ep-doc">
              <div class="ep-doc-head">
                <h3>
                  <span aria-hidden="true">📄</span> {{ doc.title }}
                  @if (doc.version) {<span class="ep-ver">{{ doc.version }}</span>}
                </h3>
                @if (finalized) {
                  <button type="button" class="ep-link" (click)="download(doc.id)">Download</button>
                }
              </div>
              <ol class="ep-list">
                @for (e of doc.embeds; track $index) {
                  <li class="ep-item">
                    <div class="ep-item-head">
                      <span class="ep-page">{{ e.page ? 'Page ' + e.page : 'End of document' }}</span>
                      <span class="ep-clause">Clause {{ e.clauseNo }}@if (e.clauseTitle) { - {{ e.clauseTitle }} }</span>
                    </div>
                    @if (!hideActions) {
                      <p class="ep-label">Action</p>
                      <p class="ep-action">{{ e.action }}</p>
                    }
                    @if (finalized && e.embeddedText) {
                      <p class="ep-label">Embedded text</p>
                      <p class="ep-action ep-embedded">{{ e.embeddedText }}</p>
                    }
                  </li>
                }
              </ol>
            </section>
          }
          @if (!finalized) {
            <p class="ep-muted ep-note">The policy wording is written by AI when the report is finalized.</p>
          }
        }
      </div>
    </aside>
  `,
  styles: [
    `
      .ep-backdrop { position: fixed; inset: 0; background: rgb(0 0 0 / 0.35); z-index: 1000; }
      .ep-drawer {
        position: fixed; top: 0; right: 0; bottom: 0; width: min(32rem, 100vw); z-index: 1001;
        display: flex; flex-direction: column; background: var(--card, var(--surface, #fff));
        border-left: 1px solid var(--border); box-shadow: -8px 0 24px rgb(0 0 0 / 0.15);
      }
      .ep-head {
        display: flex; justify-content: space-between; align-items: flex-start; gap: 1rem;
        padding: 1rem 1.25rem; border-bottom: 1px solid var(--border);
      }
      .ep-head h2 { margin: 0; font-size: 1.05rem; }
      .ep-sub { margin: 0.2rem 0 0; font-size: 0.8125rem; color: var(--muted-foreground, var(--muted)); }
      .ep-head-actions { display: flex; align-items: center; gap: 0.5rem; flex: none; }
      .ep-link { border: 0; background: none; color: var(--accent); cursor: pointer; font: inherit; font-size: 0.8125rem; }
      .ep-link:disabled { opacity: 0.6; cursor: default; }
      .ep-close { border: 0; background: none; cursor: pointer; font-size: 1rem; color: inherit; padding: 0.25rem; }
      .ep-body { overflow-y: auto; padding: 1rem 1.25rem 1.5rem; }
      .ep-summary { margin: 0 0 0.75rem; font-weight: 600; font-size: 0.875rem; }
      .ep-doc { margin-bottom: 1.1rem; }
      .ep-doc-head { display: flex; justify-content: space-between; align-items: baseline; gap: 0.75rem; margin-bottom: 0.5rem; }
      .ep-doc h3 { margin: 0; font-size: 0.9375rem; overflow-wrap: anywhere; }
      .ep-label { margin: 0.5rem 0 0; font-size: 0.6875rem; font-weight: 600; text-transform: uppercase; letter-spacing: 0.04em; color: var(--muted-foreground, var(--muted)); }
      .ep-embedded { padding-left: 0.6rem; border-left: 3px solid var(--accent); }
      .ep-ver { margin-left: 0.35rem; font-size: 0.75rem; font-weight: 500; color: var(--muted-foreground, var(--muted)); }
      .ep-list { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 0.5rem; }
      .ep-item { border: 1px solid var(--border); border-radius: 0.5rem; padding: 0.6rem 0.75rem; }
      .ep-item-head { display: flex; flex-wrap: wrap; align-items: center; gap: 0.5rem; }
      .ep-page {
        font-size: 0.75rem; font-weight: 600; padding: 0.1rem 0.5rem; border-radius: 999px;
        background: color-mix(in srgb, var(--accent) 14%, transparent); color: var(--accent);
      }
      .ep-clause { font-size: 0.8125rem; font-weight: 600; }
      .ep-action { margin: 0.15rem 0 0; font-size: 0.8125rem; line-height: 1.45; white-space: pre-wrap; }
      .ep-muted { color: var(--muted-foreground, var(--muted)); font-size: 0.875rem; }
      .ep-note { margin-top: 0.5rem; font-size: 0.8125rem; }
      .ep-error { color: var(--destructive, #c0392b); font-size: 0.875rem; }
    `,
  ],
})
export class NdEmbedPreviewDrawerComponent implements OnChanges {
  private readonly api = inject(NdApiService);
  private readonly cdr = inject(ChangeDetectorRef);

  @Input({ required: true }) runId!: string;
  /** The run is finalized: show the finalized documents and their embedded text instead of the preview. */
  @Input() finalized = false;
  /** Actions module off: show only document, page and clause. */
  @Input() hideActions = false;
  @Output() readonly closed = new EventEmitter<void>();

  docs: PreviewDoc[] = [];
  loading = false;
  error = '';

  get total(): number {
    return this.docs.reduce((sum, d) => sum + d.embeds.length, 0);
  }

  @HostListener('document:keydown.escape')
  onEscape(): void {
    this.closed.emit();
  }

  ngOnChanges(): void {
    void this.load();
  }

  async load(): Promise<void> {
    if (!this.runId) return;
    this.loading = true;
    this.error = '';
    this.cdr.markForCheck();
    const res = this.finalized
      ? await this.api.getGeneratedDocumentsForRun(this.runId)
      : await this.api.getEmbedPreviewForRun(this.runId);
    this.loading = false;
    if (res.success) {
      this.docs = (res.data ?? []).map((d) => ({ id: d.id, title: d.title, version: d.version, embeds: d.embeds ?? [] }));
    }
    else this.error = res.message ?? 'Could not load the embedding details.';
    this.cdr.markForCheck();
  }

  download(docId: string): void {
    void this.api.downloadInternalFileExport(docId);
  }
}
