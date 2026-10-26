import { Component, ElementRef, EventEmitter, HostListener, Input, Output, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';

/** One option of a tree dropdown, in tree order (parents before their children). */
export type NdTreeSelectOption = { id: string; name: string; parentId?: string | null; level: number; path: string };

/**
 * Filter dropdown for a category tree (rulebook categories, departments): a collapsible, searchable list in place
 * of a native select, which cannot nest or keep indentation. Picking a group selects that group; the caller decides
 * whether that includes what is under it.
 */
@Component({
  selector: 'app-nd-tree-select',
  standalone: true,
  imports: [FormsModule],
  template: `
    <div class="ts-wrap">
      <button
        type="button"
        class="filter-select ts-trigger"
        [attr.aria-label]="ariaLabel"
        [attr.aria-expanded]="open"
        (click)="toggleOpen()"
      >
        <span class="ts-value">{{ selectedLabel }}</span>
      </button>
      @if (open) {
        <div class="ts-panel card-surface" role="listbox" [attr.aria-label]="ariaLabel">
          <input
            type="search"
            class="filter-search ts-search"
            [(ngModel)]="search"
            placeholder="Search…"
            aria-label="Search"
          />
          <div class="ts-tools">
            <button type="button" class="filter-clear" (click)="expandAll()">Expand all</button>
            <button type="button" class="filter-clear" (click)="collapseAll()">Collapse all</button>
          </div>
          <ul class="ts-list">
            <li>
              <button type="button" class="ts-option ts-all" [class.selected]="!value" (click)="pick('')">
                {{ allLabel }}
              </button>
            </li>
            @for (o of visibleOptions; track o.id) {
              <li class="ts-row" [style.padding-left.rem]="flat ? 0 : (o.level - 1) * 1.1">
                @if (!flat && hasChildren(o.id)) {
                  <button
                    type="button"
                    class="ts-caret"
                    [class.expanded]="expanded.has(o.id)"
                    [attr.aria-label]="(expanded.has(o.id) ? 'Collapse ' : 'Expand ') + o.name"
                    (click)="toggleGroup(o.id)"
                  >▸</button>
                } @else if (!flat) {
                  <span class="ts-caret" aria-hidden="true"></span>
                }
                <button
                  type="button"
                  class="ts-option"
                  role="option"
                  [class.selected]="value === o.id"
                  [class.ts-top]="o.level === 1"
                  [attr.aria-selected]="value === o.id"
                  [title]="o.path"
                  (click)="pick(o.id)"
                >
                  {{ flat ? o.path : o.name }}
                </button>
              </li>
            } @empty {
              <li class="ts-none">Nothing matches.</li>
            }
          </ul>
        </div>
      }
    </div>
  `,
  styles: [
    `
      :host { display: inline-block; position: relative; }
      .ts-wrap { position: relative; }
      .ts-trigger { min-width: 13rem; max-width: 20rem; text-align: left; cursor: pointer; }
      .ts-value { display: block; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
      .ts-panel {
        position: absolute; top: calc(100% + 0.25rem); left: 0; z-index: 60;
        width: min(26rem, 90vw); max-height: 26rem; display: flex; flex-direction: column; gap: 0.4rem;
        padding: 0.5rem; box-shadow: 0 10px 30px rgb(0 0 0 / 0.18);
      }
      .ts-search { width: 100%; }
      .ts-tools { display: flex; gap: 0.5rem; }
      .ts-list { list-style: none; margin: 0; padding: 0; overflow: auto; flex: 1; }
      .ts-row { display: flex; align-items: center; gap: 0.15rem; }
      .ts-caret {
        flex: 0 0 1.1rem; width: 1.1rem; padding: 0; border: 0; background: none; color: var(--text-muted);
        font-size: 0.75rem; cursor: pointer; transition: transform 0.15s ease;
      }
      .ts-caret.expanded { transform: rotate(90deg); }
      .ts-option {
        flex: 1; min-width: 0; padding: 0.3rem 0.45rem; border: 0; border-radius: var(--radius-sm, 4px);
        background: none; color: var(--text); font: inherit; font-size: 0.8125rem; text-align: left; cursor: pointer;
      }
      .ts-option:hover { background: color-mix(in srgb, var(--accent) 10%, transparent); }
      .ts-option.selected { background: var(--accent); color: var(--bg-card, #fff); }
      .ts-top { font-weight: 650; }
      .ts-all { width: 100%; }
      .ts-none { padding: 0.5rem; color: var(--text-muted); font-size: 0.8125rem; }
    `,
  ],
})
export class NdTreeSelectComponent {
  private readonly host = inject(ElementRef<HTMLElement>);

  @Input() options: NdTreeSelectOption[] = [];
  @Input() value = '';
  @Input() allLabel = 'All';
  @Input() ariaLabel = 'Filter';
  @Output() valueChange = new EventEmitter<string>();

  open = false;
  search = '';
  expanded = new Set<string>();

  get selectedLabel(): string {
    return this.options.find((o) => o.id === this.value)?.name ?? this.allLabel;
  }

  get flat(): boolean {
    return !!this.search.trim();
  }

  hasChildren(id: string): boolean {
    return this.options.some((o) => o.parentId === id);
  }

  get visibleOptions(): NdTreeSelectOption[] {
    const q = this.search.trim().toLowerCase();
    if (q) return this.options.filter((o) => o.path.toLowerCase().includes(q));
    const shown = new Set<string>();
    return this.options.filter((o) => {
      const parent = o.parentId ?? null;
      const show = !parent || (shown.has(parent) && this.expanded.has(parent));
      if (show) shown.add(o.id);
      return show;
    });
  }

  toggleOpen(): void {
    this.open = !this.open;
    if (this.open) {
      this.search = '';
      // Open the groups leading to the current choice.
      let parent = this.options.find((o) => o.id === this.value)?.parentId ?? null;
      while (parent) {
        this.expanded.add(parent);
        parent = this.options.find((o) => o.id === parent)?.parentId ?? null;
      }
    }
  }

  toggleGroup(id: string): void {
    if (this.expanded.has(id)) this.expanded.delete(id);
    else this.expanded.add(id);
  }

  expandAll(): void {
    this.expanded = new Set(this.options.filter((o) => this.hasChildren(o.id)).map((o) => o.id));
  }

  collapseAll(): void {
    this.expanded = new Set();
  }

  pick(id: string): void {
    this.value = id;
    this.valueChange.emit(id);
    this.open = false;
  }

  @HostListener('document:click', ['$event'])
  onDocumentClick(event: MouseEvent): void {
    if (this.open && !this.host.nativeElement.contains(event.target as Node)) this.open = false;
  }

  @HostListener('document:keydown.escape')
  onEscape(): void {
    this.open = false;
  }
}
