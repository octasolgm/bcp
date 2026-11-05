import { Component, ElementRef, EventEmitter, HostListener, Input, Output, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';

export type NdTypeaheadOption = { id: string; label: string; hint?: string; role?: string };

export type NdTypeaheadRolePick = { id: string; role: string };

/** Type to filter a list and tick several items, then add them all with one click. */
@Component({
  selector: 'app-nd-typeahead-picker',
  standalone: true,
  imports: [FormsModule],
  template: `
    <div class="ta-row">
      <div class="ta-field">
        <input
          type="search"
          class="nd-input"
          [(ngModel)]="query"
          [placeholder]="placeholder"
          [attr.aria-label]="placeholder"
          [disabled]="busy"
          (focus)="open = true"
          (click)="open = true"
          (input)="open = true"
        />
        @if (open) {
          <div class="ta-drop card-surface">
          <ul class="ta-list" role="listbox" aria-multiselectable="true" [style.max-height.px]="listHeight">
            @if (matches.length) {
              <li class="ta-head">{{ matches.length }} available to add. Tick items{{ roleOptions.length ? ', choose each role below the list,' : ',' }} then click {{ addLabel }}.</li>
            }
            @for (o of matches; track o.id) {
              <li>
                <label>
                  <input type="checkbox" [checked]="picked.has(o.id)" (change)="toggle(o.id)" />
                  <span class="ta-label">{{ o.label }}</span>
                  @if (o.hint) { <span class="ta-hint">{{ o.hint }}</span> }
                </label>
              </li>
            } @empty {
              <li class="ta-none">{{ query.trim() ? 'No matches' : emptyLabel }}</li>
            }
          </ul>
          @if (matches.length > 3) {
            <div
              class="ta-resize"
              role="separator"
              aria-orientation="horizontal"
              aria-label="Resize list. Use the up and down arrow keys."
              title="Drag to change the height of the list"
              tabindex="0"
              (pointerdown)="startResize($event)"
              (keydown.arrowup)="nudge(-40, $event)"
              (keydown.arrowdown)="nudge(40, $event)"
            ></div>
          }
          </div>
        }
      </div>
      <button type="button" class="nd-btn-primary" [disabled]="busy || !picked.size" (click)="submit()">
        {{ busy ? 'Adding…' : addLabel + (picked.size ? ' (' + picked.size + ')' : '') }}
      </button>
    </div>
    @if (picked.size && roleOptions.length) {
      <ul class="ta-picked" aria-label="Selected, with the role for each">
        @for (o of pickedOptions; track o.id) {
          <li>
            <span class="ta-picked-name">
              <strong>{{ o.label }}</strong>
              @if (o.hint) { <span class="ta-hint-inline">{{ o.hint }}</span> }
            </span>
            <select
              class="ta-role"
              [ngModel]="roleOf(o)"
              [ngModelOptions]="{ standalone: true }"
              [attr.aria-label]="'Role of ' + o.label"
              [disabled]="busy"
              (ngModelChange)="roles.set(o.id, $event)"
            >
              @for (r of roleOptions; track r.value) {
                <option [value]="r.value">{{ r.label }}</option>
              }
            </select>
            <button type="button" class="ta-picked-remove" (click)="toggle(o.id)" [attr.aria-label]="'Remove ' + o.label">×</button>
          </li>
        }
      </ul>
    } @else if (picked.size) {
      <div class="ta-chips">
        @for (o of pickedOptions; track o.id) {
          <span class="ta-chip">
            {{ o.label }}
            <button type="button" (click)="toggle(o.id)" [attr.aria-label]="'Remove ' + o.label">×</button>
          </span>
        }
      </div>
    }
  `,
  styles: `
    .ta-row { display: flex; flex-wrap: wrap; gap: 0.5rem; align-items: flex-start; }
    .ta-field { position: relative; flex: 1 1 16rem; min-width: 0; }
    .ta-field .nd-input { width: 100%; }
    .ta-drop { margin-top: 0.25rem; overflow: hidden; }
    .ta-list { list-style: none; margin: 0; padding: 0.25rem 0; overflow: auto; }
    .ta-resize {
      height: 0.6rem; cursor: ns-resize; touch-action: none; border-top: 1px solid var(--border);
      background: linear-gradient(var(--text-muted), var(--text-muted)) center / 2rem 2px no-repeat;
    }
    .ta-resize:hover, .ta-resize:focus-visible { background-color: color-mix(in srgb, var(--accent) 12%, transparent); outline: none; }
    .ta-list label { display: flex; align-items: center; gap: 0.5rem; padding: 0.35rem 0.6rem; font-size: 0.8125rem; cursor: pointer; }
    .ta-list label:hover { background: color-mix(in srgb, var(--accent) 6%, transparent); }
    .ta-label { overflow-wrap: anywhere; }
    .ta-hint { margin-left: auto; color: var(--text-muted); font-size: 0.75rem; }
    .ta-head { padding: 0.3rem 0.6rem; color: var(--text-muted); font-size: 0.75rem; }
    .ta-none { padding: 0.5rem 0.6rem; color: var(--text-muted); font-size: 0.8125rem; }
    .ta-picked {
      list-style: none; margin: 0.5rem 0 0; padding: 0;
      border: 1px solid var(--border); border-radius: var(--radius-md); background: var(--bg-card);
    }
    .ta-picked li {
      display: flex; flex-wrap: wrap; align-items: center; gap: 0.6rem; padding: 0.4rem 0.6rem;
      border-bottom: 1px solid var(--border); font-size: 0.8125rem;
    }
    .ta-picked li:last-child { border-bottom: 0; }
    .ta-picked-name { flex: 1 1 12rem; min-width: 0; display: flex; flex-direction: column; overflow-wrap: anywhere; }
    .ta-hint-inline { color: var(--text-muted); font-size: 0.75rem; }
    .ta-role {
      height: 34px; padding: 0 0.5rem; border: 1px solid var(--border-strong); border-radius: var(--radius-md);
      background: var(--bg-input); color: var(--text-primary); font: inherit; font-size: 0.8125rem;
    }
    .ta-picked-remove { padding: 0 0.25rem; border: 0; background: none; color: var(--text-muted); cursor: pointer; font-size: 1rem; line-height: 1; }
    .ta-chips { display: flex; flex-wrap: wrap; gap: 0.35rem; margin-top: 0.5rem; }
    .ta-chip {
      display: inline-flex; align-items: center; gap: 0.3rem; padding: 0.1rem 0.5rem; font-size: 0.75rem;
      border: 1px solid var(--border); border-radius: 999px;
    }
    .ta-chip button { padding: 0; border: 0; background: none; color: var(--text-muted); cursor: pointer; font-size: 0.875rem; line-height: 1; }
  `,
})
export class NdTypeaheadPickerComponent {
  private readonly host = inject(ElementRef<HTMLElement>);

  @Input() options: NdTypeaheadOption[] = [];
  @Input() placeholder = 'Type to search…';
  @Input() addLabel = 'Add';
  @Input() emptyLabel = 'Nothing left to add';
  @Input() busy = false;
  /** When set, each ticked item gets its own role dropdown and addWithRoles fires instead of add. */
  @Input() roleOptions: Array<{ value: string; label: string }> = [];
  @Output() add = new EventEmitter<string[]>();
  @Output() addWithRoles = new EventEmitter<NdTypeaheadRolePick[]>();

  query = '';
  /** The list shows as soon as the picker appears; typing then narrows it. */
  open = true;
  picked = new Set<string>();
  /** Roles changed from the starting value; everyone else keeps the role they already have. */
  roles = new Map<string, string>();
  /** Tallest the list grows before it scrolls; the handle under it changes this. */
  listHeight = 240;

  startResize(event: PointerEvent): void {
    event.preventDefault();
    const startY = event.clientY;
    const startHeight = this.listHeight;
    const move = (e: PointerEvent) => this.setHeight(startHeight + e.clientY - startY);
    const up = () => {
      window.removeEventListener('pointermove', move);
      window.removeEventListener('pointerup', up);
    };
    window.addEventListener('pointermove', move);
    window.addEventListener('pointerup', up);
  }

  nudge(by: number, event: Event): void {
    event.preventDefault();
    this.setHeight(this.listHeight + by);
  }

  private setHeight(height: number): void {
    this.listHeight = Math.min(600, Math.max(80, height));
  }

  @HostListener('document:click', ['$event'])
  onDocumentClick(event: Event): void {
    if (!this.host.nativeElement.contains(event.target as Node)) this.open = false;
  }

  /** Esc closes the open list first; the page sees the handled event and leaves itself open. */
  @HostListener('document:keydown.escape', ['$event'])
  onEscape(event: Event): void {
    if (!this.open) return;
    this.open = false;
    event.preventDefault();
  }

  get matches(): NdTypeaheadOption[] {
    const q = this.query.trim().toLowerCase();
    if (!q) return this.options;
    return this.options.filter((o) => o.label.toLowerCase().includes(q) || (o.hint ?? '').toLowerCase().includes(q));
  }

  get pickedOptions(): NdTypeaheadOption[] {
    return this.options.filter((o) => this.picked.has(o.id));
  }

  toggle(id: string): void {
    if (this.picked.has(id)) this.picked.delete(id);
    else this.picked.add(id);
  }

  /** Starts on the role the person already has, so keeping it is one less step. */
  roleOf(option: NdTypeaheadOption): string {
    return this.roles.get(option.id) ?? option.role ?? this.roleOptions[0]?.value ?? '';
  }

  submit(): void {
    if (this.roleOptions.length) this.addWithRoles.emit(this.pickedOptions.map((o) => ({ id: o.id, role: this.roleOf(o) })));
    else this.add.emit([...this.picked]);
    this.picked = new Set();
    this.roles.clear();
    this.query = '';
    this.open = false;
  }
}
