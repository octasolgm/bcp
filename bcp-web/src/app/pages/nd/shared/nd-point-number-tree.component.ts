import {
  Component,
  EventEmitter,
  forwardRef,
  Input,
  OnChanges,
  Output,
  SimpleChanges,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { formatSectionGroupLabel } from '../../../../lib/gov-point-filter';
import type { LibraryPointDisplayRow, PointDisplayTreeNode } from '../../../../lib/library-points-utils';

@Component({
  selector: 'app-nd-point-number-tree',
  standalone: true,
  imports: [CommonModule, forwardRef(() => NdPointNumberTreeComponent)],
  templateUrl: './nd-point-number-tree.component.html',
  styleUrl: './nd-point-number-tree.component.scss',
})
export class NdPointNumberTreeComponent implements OnChanges {
  @Input({ required: true }) nodes: PointDisplayTreeNode[] = [];
  @Input() baseDepth = 0;
  @Input() mode: 'analysis' | 'view' = 'analysis';
  @Input() selected = new Set<string>();
  @Input() selectedDetailPointId: string | null = null;
  @Input() resolvePointId: (row: LibraryPointDisplayRow) => string | null = (row) =>
    row.forAnalysis ? row.point.point_id : null;

  @Output() togglePoint = new EventEmitter<string>();
  @Output() toggleGroup = new EventEmitter<string[]>();
  @Output() selectDetail = new EventEmitter<string>();

  expandedKeys = new Set<string>();
  readonly formatSectionGroupLabel = formatSectionGroupLabel;

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['nodes']) {
      this.expandedKeys = new Set(
        this.nodes.filter((node) => node.children.length > 0).map((node) => node.key),
      );
    }
  }

  truncate(text: string, max: number): string {
    const t = text.trim();
    if (t.length <= max) return t;
    return `${t.slice(0, max)}…`;
  }

  groupLabel(node: PointDisplayTreeNode): string {
    return this.formatSectionGroupLabel(node.displayId);
  }

  isExpanded(key: string): boolean {
    return this.expandedKeys.has(key);
  }

  toggleExpand(key: string): void {
    const next = new Set(this.expandedKeys);
    if (next.has(key)) next.delete(key);
    else next.add(key);
    this.expandedKeys = next;
  }

  selectableIds(node: PointDisplayTreeNode): string[] {
    const ids: string[] = [];
    this.walkSelectable(node, ids);
    return ids;
  }

  groupSelectedCount(node: PointDisplayTreeNode): number {
    return this.selectableIds(node).filter((id) => this.selected.has(id)).length;
  }

  groupAllSelected(node: PointDisplayTreeNode): boolean {
    const ids = this.selectableIds(node);
    return ids.length > 0 && ids.every((id) => this.selected.has(id));
  }

  toggleGroupSelection(node: PointDisplayTreeNode, event: Event): void {
    event.stopPropagation();
    this.toggleGroup.emit(this.selectableIds(node));
  }

  onToggle(pointId: string): void {
    this.togglePoint.emit(pointId);
  }

  onSelectDetail(pointId: string, event: Event): void {
    event.preventDefault();
    this.selectDetail.emit(pointId);
  }

  private walkSelectable(node: PointDisplayTreeNode, ids: string[]): void {
    if (node.row) {
      const pid = this.resolvePointId(node.row);
      if (pid) ids.push(pid);
    }
    for (const child of node.children) {
      this.walkSelectable(child, ids);
    }
  }
}
