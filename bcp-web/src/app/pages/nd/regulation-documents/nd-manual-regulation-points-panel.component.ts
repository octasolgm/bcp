import { Component, EventEmitter, Input, OnChanges, Output, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { NdApiService } from '../../../services/nd/nd-api.service';
import type { RegulationPoint } from '../../../../lib/nd/types';

type ManualPointForm = {
  pointTitle: string;
  pointContent: string;
  pageReference: string;
};

type TreeNode = RegulationPoint & { depth: number; children: TreeNode[] };

@Component({
  selector: 'app-nd-manual-regulation-points-panel',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './nd-manual-regulation-points-panel.component.html',
  styleUrls: ['./nd-manual-regulation-points-panel.component.scss', '../nd-shared.scss'],
})
export class NdManualRegulationPointsPanelComponent implements OnChanges {
  private readonly api = inject(NdApiService);

  @Input() docId = '';
  @Input() docName = '';
  @Input() points: RegulationPoint[] = [];
  @Input() loading = false;
  @Output() pointsChanged = new EventEmitter<void>();

  form: ManualPointForm = { pointTitle: '', pointContent: '', pageReference: '' };
  selectedParent: string | null = null;
  saving = false;
  deletingId: string | null = null;
  editingId: string | null = null;
  editingPointNumber = '';
  error = '';
  message = '';

  ngOnChanges(): void {
    this.error = '';
    this.message = '';
  }

  get treeRoots(): TreeNode[] {
    return this.buildTree(this.points);
  }

  get previewNumber(): string {
    return this.allocateNextNumber(this.selectedParent);
  }

  get parentLabel(): string {
    return this.selectedParent ? `under ${this.selectedParent}` : 'as main point';
  }

  async addPoint(): Promise<void> {
    if (!this.docId || !this.form.pointContent.trim()) return;
    this.saving = true;
    this.error = '';
    this.message = '';

    const body = {
      parentPointNumber: this.editingId ? undefined : this.selectedParent,
      pointTitle: this.form.pointTitle.trim() || null,
      pointContent: this.form.pointContent.trim(),
      pageReference: this.form.pageReference.trim() || null,
    };

    const res = this.editingId
      ? await this.api.updateManualRegulationPoint(this.docId, this.editingId, body)
      : await this.api.createManualRegulationPoint(this.docId, body);

    if (res.success) {
      this.message = this.editingId ? 'Point updated' : `Point ${this.previewNumber} added`;
      this.resetForm();
      this.pointsChanged.emit();
    } else {
      this.error = this.shortError(res.message ?? (this.editingId ? 'Failed to update point' : 'Failed to add point'));
    }
    this.saving = false;
  }

  selectParent(pointNumber: string | null): void {
    if (this.editingId) return;
    this.selectedParent = pointNumber;
    this.error = '';
  }

  startEdit(point: RegulationPoint): void {
    this.editingId = point.id;
    this.editingPointNumber = point.pointNumber;
    this.selectedParent = this.parentOf(point.pointNumber);
    this.form = {
      pointTitle: point.pointTitle ?? '',
      pointContent: point.pointContent,
      pageReference: point.pageReference ?? '',
    };
    this.error = '';
    this.message = '';
  }

  cancelEdit(): void {
    this.resetForm();
  }

  async deletePoint(point: RegulationPoint): Promise<void> {
    if (!this.docId || !confirm(`Delete point ${point.pointNumber}?`)) return;
    this.deletingId = point.id;
    this.error = '';
    const res = await this.api.deleteManualRegulationPoint(this.docId, point.id);
    if (res.success) {
      this.message = 'Point deleted';
      if (this.selectedParent === point.pointNumber) this.selectedParent = null;
      this.pointsChanged.emit();
    } else {
      this.error = this.shortError(res.message ?? 'Failed to delete point');
    }
    this.deletingId = null;
  }

  canAddChild(pointNumber: string): boolean {
    return pointNumber.trim().replace(/\.$/, '').split('.').length < 3;
  }

  depthLabel(depth: number): string {
    if (depth === 1) return 'Main';
    if (depth === 2) return 'Sub';
    return 'Child';
  }

  private resetForm(): void {
    this.editingId = null;
    this.editingPointNumber = '';
    this.form = { pointTitle: '', pointContent: '', pageReference: '' };
  }

  private shortError(msg: string): string {
    const line = msg.split('\n').find((l) => l.trim()) ?? msg;
    if (line.length > 220) return `${line.slice(0, 220)}…`;
    return line;
  }

  private buildTree(points: RegulationPoint[]): TreeNode[] {
    const sorted = [...points].sort((a, b) => this.comparePointNumbers(a.pointNumber, b.pointNumber));
    const nodes = new Map<string, TreeNode>();
    const roots: TreeNode[] = [];

    for (const p of sorted) {
      const depth = p.pointNumber.trim().replace(/\.$/, '').split('.').length;
      const node: TreeNode = { ...p, depth, children: [] };
      nodes.set(p.pointNumber, node);
    }

    for (const node of nodes.values()) {
      const parent = this.parentOf(node.pointNumber);
      if (parent && nodes.has(parent)) {
        nodes.get(parent)!.children.push(node);
      } else {
        roots.push(node);
      }
    }

    return roots;
  }

  private allocateNextNumber(parent: string | null): string {
    const numbers = this.points.map((p) => p.pointNumber.trim().replace(/\.$/, ''));
    if (!parent) {
      const next =
        numbers
          .map((n) => parseInt(n.split('.')[0], 10))
          .filter((n) => !Number.isNaN(n))
          .reduce((max, n) => Math.max(max, n), 0) + 1;
      return String(next);
    }
    const normalizedParent = parent.trim().replace(/\.$/, '');
    const siblingNext =
      numbers
        .filter((n) => this.parentOf(n) === normalizedParent)
        .map((n) => parseInt(n.split('.').pop() ?? '0', 10))
        .filter((n) => !Number.isNaN(n))
        .reduce((max, n) => Math.max(max, n), 0) + 1;
    return `${normalizedParent}.${siblingNext}`;
  }

  private parentOf(pointNumber: string): string | null {
    const parts = pointNumber.trim().replace(/\.$/, '').split('.');
    if (parts.length <= 1) return null;
    return parts.slice(0, -1).join('.');
  }

  private comparePointNumbers(a: string, b: string): number {
    const ap = a.trim().replace(/\.$/, '').split('.').map(Number);
    const bp = b.trim().replace(/\.$/, '').split('.').map(Number);
    for (let i = 0; i < Math.max(ap.length, bp.length); i++) {
      const av = ap[i] ?? 0;
      const bv = bp[i] ?? 0;
      if (av !== bv) return av - bv;
    }
    return 0;
  }
}
