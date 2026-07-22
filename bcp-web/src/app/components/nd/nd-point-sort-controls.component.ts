import { Component, EventEmitter, Input, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { togglePointSort, type PointSortMode } from '../../../lib/nd/point-sort';
import type { SortDir } from '../../../lib/nd/list-utils';

@Component({
  selector: 'app-nd-point-sort-controls',
  standalone: true,
  imports: [CommonModule],
  template: `
    <div class="nd-point-sort">
      <button type="button" class="filter-util" [class.active]="sort === 'number'" (click)="pick('number')">
        Sort § {{ sort === 'number' ? (dir === 'asc' ? '↑' : '↓') : '' }}
      </button>
      <button type="button" class="filter-util" [class.active]="sort === 'status'" (click)="pick('status')">
        Sort status {{ sort === 'status' ? (dir === 'asc' ? '↑' : '↓') : '' }}
      </button>
    </div>
  `,
  styles: `
    :host {
      display: contents;
    }
    .nd-point-sort {
      display: inline-flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 0.45rem;
    }
    .filter-util {
      padding: 0.4rem 0.85rem;
      border-radius: var(--radius-pill);
      font-size: 0.8125rem;
      font-weight: 500;
      border: 1px solid var(--border);
      background: transparent;
      color: var(--text-secondary);
      cursor: pointer;
      font-family: inherit;
      white-space: nowrap;
    }
    .filter-util:hover {
      border-color: var(--border-strong);
      color: var(--text-primary);
    }
    .filter-util.active {
      background: var(--filter-active-bg);
      border-color: var(--filter-active-bg);
      color: var(--filter-active-text);
    }
  `,
})
export class NdPointSortControlsComponent {
  @Input() sort: PointSortMode = 'number';
  @Input() dir: SortDir = 'asc';
  @Output() sortChange = new EventEmitter<{ sort: 'number' | 'status'; dir: SortDir }>();

  pick(next: 'number' | 'status'): void {
    const nextState = togglePointSort(this.sort, this.dir, next);
    this.sortChange.emit({ sort: next, dir: nextState.dir });
  }
}
