import { Component, EventEmitter, HostListener, Input, OnInit, Output } from '@angular/core';
import { FormsModule } from '@angular/forms';

export type NdPickerDepartment = { id: string; name: string; parentId?: string | null; isActive?: boolean };

type PickerOption = { dept: NdPickerDepartment; level: number; path: string };

/** Full name of a department with its parents, e.g. "Compliance › AML/CFT". */
export function ndDepartmentPath(all: NdPickerDepartment[], id: string): string {
  const parts: string[] = [];
  let current = all.find((d) => d.id === id);
  while (current) {
    parts.unshift(current.name);
    const parentId = current.parentId;
    current = parentId ? all.find((d) => d.id === parentId) : undefined;
  }
  return parts.join(' › ');
}

/** Dialog for choosing several departments from the three-level tree, the same way documents are filed. */
@Component({
  selector: 'app-nd-department-picker',
  standalone: true,
  imports: [FormsModule],
  template: `
    <div class="rb-picker-backdrop" (click)="closed.emit()"></div>
    <div class="rb-picker card-surface" role="dialog" aria-modal="true" [attr.aria-label]="title">
      <header class="rb-picker-head">
        <div>
          <strong>{{ title }}</strong>
          @if (subtitle) { <p class="muted">{{ subtitle }}</p> }
        </div>
        <button type="button" class="btn-close" (click)="closed.emit()" aria-label="Close">×</button>
      </header>
      <div class="rb-picker-tabs" role="tablist">
        <button type="button" role="tab" [class.active]="tab === 'all'" (click)="tab = 'all'">All departments</button>
        <button type="button" role="tab" [class.active]="tab === 'selected'" (click)="tab = 'selected'">Selected ({{ picked.size }})</button>
      </div>
      <div class="rb-picker-tools">
        <input type="search" class="filter-search rb-picker-search" [(ngModel)]="search" placeholder="Search departments…" aria-label="Search departments" />
        <button type="button" class="filter-clear" (click)="clear()">Clear</button>
        @if (!flat) {
          <button type="button" class="filter-clear" (click)="expandAll()">Expand</button>
          <button type="button" class="filter-clear" (click)="expanded = emptySet()">Collapse</button>
        }
      </div>
      <ul class="rb-picker-list">
        @for (o of visible; track o.dept.id) {
          <li [class.rb-pl-1]="!flat && o.level === 1" [class.rb-pl-2]="!flat && o.level === 2" [class.rb-pl-3]="!flat && o.level === 3">
            <label [title]="o.path">
              @if (!flat && hasChildren(o.dept.id)) {
                <button type="button" class="rb-picker-caret" [class.open]="expanded.has(o.dept.id)" (click)="toggleGroup(o.dept.id, $event)"
                  [attr.aria-label]="(expanded.has(o.dept.id) ? 'Collapse ' : 'Expand ') + o.dept.name">▸</button>
              } @else if (!flat) {
                <span class="rb-picker-caret" aria-hidden="true"></span>
              }
              <input type="checkbox" [checked]="picked.has(o.dept.id)" (change)="toggle(o.dept.id)" />
              <span>{{ flat ? o.path : o.dept.name }}</span>
            </label>
          </li>
        } @empty {
          <li class="muted rb-picker-none">{{ tab === 'selected' ? 'Nothing selected yet.' : 'No departments match.' }}</li>
        }
      </ul>
      @if (hint) { <p class="muted rb-picker-hint">{{ hint }}</p> }
      <footer class="rb-picker-foot">
        <span class="muted">{{ picked.size ? picked.size + ' selected' : emptyLabel }}</span>
        <button type="button" class="nd-btn-secondary" (click)="closed.emit()">Cancel</button>
        <button type="button" class="nd-btn-primary" [disabled]="saving" (click)="saved.emit(ids())">
          @if (saving) { <span class="rb-spinner" aria-hidden="true"></span> }{{ saving ? 'Saving…' : 'Save' }}
        </button>
      </footer>
    </div>
  `,
  styles: `
    .rb-picker-backdrop { position: fixed; inset: 0; background: rgb(0 0 0 / 0.35); z-index: 1000; }
    .rb-picker {
      position: fixed; top: 8vh; left: 50%; transform: translateX(-50%);
      width: min(36rem, calc(100vw - 2rem)); max-height: 84vh;
      display: flex; flex-direction: column; gap: 0.6rem; padding: 1rem; z-index: 1001;
      text-align: left; font-weight: 400; white-space: normal; cursor: default;
    }
    .rb-picker-head { display: flex; justify-content: space-between; gap: 1rem; }
    .rb-picker-head p { margin: 0.15rem 0 0; font-size: 0.8125rem; }
    .rb-picker-tabs { display: flex; gap: 0.25rem; border-bottom: 1px solid var(--border); }
    .rb-picker-tabs button {
      padding: 0.4rem 0.8rem; border: 0; border-bottom: 2px solid transparent; background: none;
      color: var(--text-muted); font: inherit; font-size: 0.8125rem; font-weight: 600; cursor: pointer;
    }
    .rb-picker-tabs button.active { color: var(--accent); border-bottom-color: var(--accent); }
    .rb-picker-tools { display: flex; flex-wrap: wrap; align-items: center; gap: 0.4rem; }
    .rb-picker-search { flex: 1 1 12rem; }
    .rb-picker-list {
      list-style: none; margin: 0; padding: 0; overflow: auto; flex: 1;
      border: 1px solid var(--border); border-radius: var(--radius-md);
    }
    .rb-picker-list li { border-bottom: 1px solid var(--border); }
    .rb-picker-list li:last-child { border-bottom: 0; }
    .rb-picker-list label {
      display: flex; align-items: flex-start; gap: 0.5rem; padding: 0.35rem 0.6rem; cursor: pointer; font-size: 0.8125rem;
    }
    .rb-pl-1 label { font-weight: 650; }
    .rb-pl-2 label { padding-left: 1.6rem; }
    .rb-pl-3 label { padding-left: 3rem; }
    .rb-picker-caret {
      flex: 0 0 1rem; width: 1rem; padding: 0; border: 0; background: none; color: var(--text-muted);
      font-size: 0.75rem; line-height: 1.4; cursor: pointer; transition: transform 0.15s ease;
    }
    .rb-picker-caret.open { transform: rotate(90deg); }
    .rb-picker-none { padding: 0.6rem; }
    .rb-picker-hint { margin: 0; font-size: 0.8125rem; }
    .rb-picker-foot { display: flex; align-items: center; justify-content: flex-end; gap: 0.5rem; }
    .rb-picker-foot .muted { margin-right: auto; }
    .rb-spinner {
      display: inline-block; width: 0.85rem; height: 0.85rem; margin-right: 0.4rem; vertical-align: -0.12rem;
      border: 2px solid color-mix(in srgb, currentColor 30%, transparent); border-top-color: currentColor;
      border-radius: 50%; animation: dp-spin 0.75s linear infinite;
    }
    @keyframes dp-spin { to { transform: rotate(360deg); } }
  `,
})
export class NdDepartmentPickerComponent implements OnInit {
  @Input({ required: true }) departments: NdPickerDepartment[] = [];
  @Input() selected: string[] = [];
  @Input() title = 'Departments';
  @Input() subtitle = '';
  @Input() hint = '';
  @Input() emptyLabel = 'None selected';
  @Input() saving = false;
  @Output() saved = new EventEmitter<string[]>();
  @Output() closed = new EventEmitter<void>();

  tab: 'all' | 'selected' = 'all';
  search = '';
  picked = new Set<string>();
  expanded = new Set<string>();
  private options: PickerOption[] = [];

  ngOnInit(): void {
    this.picked = new Set(this.selected);
    const out: PickerOption[] = [];
    const walk = (parentId: string | null, level: number, prefix: string) => {
      this.departments
        .filter((d) => (d.parentId ?? null) === parentId && (d.isActive !== false || this.picked.has(d.id)))
        .sort((a, b) => a.name.localeCompare(b.name))
        .forEach((dept) => {
          const path = prefix ? `${prefix} › ${dept.name}` : dept.name;
          out.push({ dept, level, path });
          if (level < 3) walk(dept.id, level + 1, path);
        });
    };
    walk(null, 1, '');
    this.options = out;
    for (const id of this.picked) {
      let parent = this.departments.find((d) => d.id === id)?.parentId ?? null;
      while (parent) {
        this.expanded.add(parent);
        const parentId: string = parent;
        parent = this.departments.find((d) => d.id === parentId)?.parentId ?? null;
      }
    }
  }

  @HostListener('document:keydown.escape')
  onEscape(): void {
    this.closed.emit();
  }

  emptySet(): Set<string> {
    return new Set<string>();
  }

  ids(): string[] {
    return [...this.picked];
  }

  get flat(): boolean {
    return !!this.search.trim() || this.tab === 'selected';
  }

  get visible(): PickerOption[] {
    const q = this.search.trim().toLowerCase();
    let list = this.options;
    if (this.tab === 'selected') list = list.filter((o) => this.picked.has(o.dept.id));
    if (q) return list.filter((o) => o.path.toLowerCase().includes(q));
    if (this.flat) return list;
    const shown = new Set<string>();
    return list.filter((o) => {
      const parent = o.dept.parentId ?? null;
      const show = !parent || (shown.has(parent) && this.expanded.has(parent));
      if (show) shown.add(o.dept.id);
      return show;
    });
  }

  hasChildren(id: string): boolean {
    return this.options.some((o) => o.dept.parentId === id);
  }

  toggleGroup(id: string, event: Event): void {
    event.preventDefault();
    event.stopPropagation();
    if (this.expanded.has(id)) this.expanded.delete(id);
    else this.expanded.add(id);
  }

  expandAll(): void {
    this.expanded = new Set(this.options.filter((o) => this.hasChildren(o.dept.id)).map((o) => o.dept.id));
  }

  clear(): void {
    this.picked = new Set();
  }

  toggle(id: string): void {
    if (this.picked.has(id)) this.picked.delete(id);
    else this.picked.add(id);
  }
}
