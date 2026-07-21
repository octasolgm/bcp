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
