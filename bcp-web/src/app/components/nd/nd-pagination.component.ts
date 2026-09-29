import { Component, EventEmitter, Input, Output } from '@angular/core';
import { FormsModule } from '@angular/forms';

/** Records-per-page + clickable page numbers + jump-to-page, for any server-paginated list. */
@Component({
  selector: 'app-nd-pagination',
  standalone: true,
  imports: [FormsModule],
  template: `
    <div class="nd-pager" [class.is-disabled]="disabled" [class.is-compact]="compact">
      <div class="nd-pager-size">
        <label>
          Show
          <select
            [ngModel]="pageSize"
            (ngModelChange)="onPageSizeChange($event)"
            [disabled]="disabled"
            aria-label="Records per page"
          >
            @for (opt of pageSizeOptions; track opt) {
              <option [value]="opt">{{ opt }}</option>
            }
          </select>
          @if (!compact) {
            per page
          }
        </label>
        @if (totalCount && !compact) {
          <span class="nd-pager-range">{{ rangeLabel }}</span>
        }
      </div>

      @if (totalPages > 1) {
        <div class="nd-pager-nav">
          <button
            type="button"
            class="nd-pager-btn"
            [disabled]="page <= 1 || disabled"
            (click)="go(page - 1)"
            aria-label="Previous page"
          >
            ‹
          </button>

          @for (item of pageItems; track $index) {
            @if (item === -1) {
              <span class="nd-pager-ellipsis">…</span>
            } @else {
              <button
                type="button"
                class="nd-pager-btn nd-pager-num"
                [class.is-active]="item === page"
                [disabled]="disabled"
                [attr.aria-current]="item === page ? 'page' : null"
                (click)="go(item)"
              >
                {{ item }}
              </button>
            }
          }

          <button
            type="button"
            class="nd-pager-btn"
            [disabled]="page >= totalPages || disabled"
            (click)="go(page + 1)"
            aria-label="Next page"
          >
            ›
          </button>

          <label class="nd-pager-jump">
            Go to
            <select [ngModel]="page" (ngModelChange)="go($event)" [disabled]="disabled" aria-label="Jump to page">
              @for (p of allPageNumbers; track p) {
                <option [value]="p">{{ p }}</option>
              }
            </select>
          </label>
        </div>
      }
    </div>
  `,
  styles: [
    `
      :host {
        display: block;
      }

      .nd-pager {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        justify-content: space-between;
        gap: 0.75rem;
        padding: 0.75rem 0;
      }

      .nd-pager.is-disabled {
        opacity: 0.6;
        pointer-events: none;
      }

      .nd-pager-size {
        display: flex;
        align-items: center;
        gap: 0.75rem;
        flex-wrap: wrap;
        font-size: 0.8125rem;
        color: var(--text-secondary);
      }

      .nd-pager-size select {
        margin: 0 0.35rem;
        padding: 0.25rem 0.4rem;
        border-radius: 6px;
        border: 1px solid var(--border);
        background: var(--bg-card);
        color: var(--text-primary);
        font-size: 0.8125rem;
      }

      .nd-pager-range {
        color: var(--text-muted);
      }

      .nd-pager-nav {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 0.3rem;
      }

      // Compact mode (e.g. inline in a filter toolbar): no "per page"/range text, and the page
      // buttons push to the right edge instead of sitting right after the size select.
      .nd-pager.is-compact {
        gap: 0.5rem;
      }

      // Push the whole group (size select + page nav) together to the row's right edge, rather
      // than just the page nav — so "Show 20" sits beside the page numbers, not stranded on the left.
      .nd-pager.is-compact .nd-pager-size {
        margin-left: auto;
      }

      .nd-pager-btn {
        min-width: 2rem;
        height: 2rem;
        padding: 0 0.5rem;
        border-radius: 6px;
        border: 1px solid var(--border);
        background: var(--bg-card);
        color: var(--text-primary);
        font-size: 0.8125rem;
        cursor: pointer;
      }

      .nd-pager-btn:hover:not(:disabled) {
        background: var(--bg-muted);
      }

      .nd-pager-btn:disabled {
        opacity: 0.45;
        cursor: default;
      }

      .nd-pager-num.is-active {
        background: var(--accent);
        border-color: var(--accent);
        color: var(--bg-card);
        font-weight: 600;
      }

      .nd-pager-ellipsis {
        padding: 0 0.25rem;
        color: var(--text-muted);
      }

      .nd-pager-jump {
        display: flex;
        align-items: center;
        gap: 0.35rem;
        font-size: 0.8125rem;
        color: var(--text-secondary);
        margin-left: 0.5rem;
      }

      .nd-pager-jump select {
        min-width: 3.75rem;
        padding: 0.25rem 0.4rem;
        border-radius: 6px;
        border: 1px solid var(--border);
        background: var(--bg-card);
        color: var(--text-primary);
        font-size: 0.8125rem;
      }

      // Narrow viewports: stack the size selector above the page controls, and drop the jump
      // input's left margin now that it's on its own row.
      @media (max-width: 640px) {
        .nd-pager {
          flex-direction: column;
          align-items: stretch;
        }

        .nd-pager-nav {
          justify-content: center;
        }

        .nd-pager-jump {
          margin-left: 0;
        }
      }
    `,
  ],
})
export class NdPaginationComponent {
  @Input() page = 1;
  @Input() pageSize = 20;
  @Input() totalCount = 0;
  @Input() totalPages = 0;
  @Input() pageSizeOptions: number[] = [20, 50, 100];
  @Input() disabled = false;
  /** Drops the "per page"/range text and pushes the page buttons to the right — for embedding
   * inline in a filter toolbar row instead of its own full-width block. */
  @Input() compact = false;

  @Output() pageChange = new EventEmitter<number>();
  @Output() pageSizeChange = new EventEmitter<number>();

  get rangeLabel(): string {
    if (!this.totalCount) return '0 records';
    const start = (this.page - 1) * this.pageSize + 1;
    const end = Math.min(this.page * this.pageSize, this.totalCount);
    return `${start}–${end} of ${this.totalCount}`;
  }

  /** First page, last page, current ±1 — everything else collapses into a single "…" (-1) on
   * each side, so the control stays a fixed, predictable width regardless of how many pages exist. */
  get pageItems(): number[] {
    const total = this.totalPages;
    const current = this.page;
    if (total <= 7) return Array.from({ length: total }, (_, i) => i + 1);
    const items: number[] = [1];
    if (current > 3) items.push(-1);
    for (let p = Math.max(2, current - 1); p <= Math.min(total - 1, current + 1); p++) items.push(p);
    if (current < total - 2) items.push(-1);
    items.push(total);
    return items;
  }

  /** allPageNumbers backs the "Go to" dropdown — every page, not the collapsed pageItems list. */
  get allPageNumbers(): number[] {
    return Array.from({ length: this.totalPages }, (_, i) => i + 1);
  }

  go(next: number | string): void {
    const n = Number(next);
    if (this.disabled || !n || n < 1 || n > this.totalPages || n === this.page) return;
    this.pageChange.emit(n);
  }

  onPageSizeChange(size: number): void {
    const n = Number(size);
    if (!n || n === this.pageSize) return;
    this.pageSizeChange.emit(n);
  }
}
