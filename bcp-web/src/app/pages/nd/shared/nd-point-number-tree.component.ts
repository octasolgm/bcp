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
import {
  formatSectionGroupLabel,
  sectionHeadingTitleForKey,
  type GovPoint,
} from '../../../../lib/gov-point-filter';
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
  @Input() showRichDetails = true;
  @Input() catalogPoints: GovPoint[] = [];
  @Input() resolvePointId: (row: LibraryPointDisplayRow) => string | null = (row) =>
    row.forAnalysis ? row.point.point_id : null;

  @Output() togglePoint = new EventEmitter<string>();
  @Output() toggleGroup = new EventEmitter<string[]>();
  @Output() selectGroup = new EventEmitter<string[]>();
  @Output() clearGroup = new EventEmitter<string[]>();
  @Output() selectDetail = new EventEmitter<string>();

  expandedKeys = new Set<string>();
  expandedPoints = new Set<string>();
  readonly previewLen = 120;
  private resolvedCatalog: GovPoint[] = [];

  readonly formatSectionGroupLabel = formatSectionGroupLabel;

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['nodes'] || changes['catalogPoints']) {
      this.resolvedCatalog = this.catalogPoints.length
        ? this.catalogPoints
        : this.collectCatalogFromNodes(this.nodes);
    }
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
    const fromCatalog = sectionHeadingTitleForKey(node.displayId, this.resolvedCatalog);
    const headerTitle =
      fromCatalog ||
      (node.children.length > 0 && node.row && !this.resolvePointId(node.row)
        ? node.row.point.title?.trim()
        : null);
    return this.formatSectionGroupLabel(node.displayId, headerTitle);
  }

  hasSeparateTitle(point: GovPoint): boolean {
    const title = (point.title ?? '').trim();
    const text = (point.text ?? '').trim();
    return !!(title && text && title !== text);
  }

  pointTitle(point: GovPoint): string {
    return (point.title ?? '').trim();
  }

  pointDetail(point: GovPoint): string {
    const title = (point.title ?? '').trim();
    const text = (point.text ?? '').trim();
    if (title && text && title !== text) return text;
    if (!title && text) return text;
    return '';
  }

  showDetail(point: GovPoint): boolean {
    return this.pointDetail(point).length > 0;
  }

  isDetailLong(point: GovPoint): boolean {
    return this.pointDetail(point).length > this.previewLen;
  }

  pointRowKey(row: LibraryPointDisplayRow): string {
    return row.point.regulationPointId ?? row.point.point_id ?? row.displayId;
  }

  isPointExpanded(key: string): boolean {
    return this.expandedPoints.has(key);
  }

  togglePointText(key: string, event?: Event): void {
    event?.stopPropagation();
    event?.preventDefault();
    if (this.expandedPoints.has(key)) this.expandedPoints.delete(key);
    else this.expandedPoints.add(key);
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

  /** Some but not all children picked — drives the parent checkbox's dash state. */
  groupPartlySelected(node: PointDisplayTreeNode): boolean {
    const count = this.groupSelectedCount(node);
    return count > 0 && count < this.selectableIds(node).length;
  }

  /**
   * Select or clear every point under a section. Explicit rather than a toggle so the
   * parent checkbox and the All/Clear buttons all land on a predictable result.
   */
  setGroupSelection(node: PointDisplayTreeNode, select: boolean, event?: Event): void {
    event?.stopPropagation();
    const ids = this.selectableIds(node);
    if (!ids.length) return;
    if (select) this.selectGroup.emit(ids);
    else this.clearGroup.emit(ids);
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

  private collectCatalogFromNodes(nodes: PointDisplayTreeNode[]): GovPoint[] {
    const out: GovPoint[] = [];
    const seen = new Set<string>();
    const walk = (list: PointDisplayTreeNode[]) => {
      for (const node of list) {
        if (node.row) {
          const p = node.row.point;
          const key = `${p.point_id}|${p.section ?? ''}|${p.title ?? ''}`;
          if (!seen.has(key)) {
            seen.add(key);
            out.push(p);
          }
        }
        if (node.children.length) walk(node.children);
      }
    };
    walk(nodes);
    return out;
  }
}
