import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { NdApiService } from '../../../services/nd/nd-api.service';
import { NdAuthService } from '../../../services/nd/nd-auth.service';
import {
  compareText,
  hasListFilters,
  matchesSearch,
  nextSortState,
  sortIndicator,
  type SortDir,
} from '../../../../lib/nd/list-utils';

export type SynonymEntry = {
  id: string;
  termA: string;
  termB: string;
  source: 'auto' | 'manual';
  sourceDocumentId: string | null;
  sourceDocumentName: string | null;
  sourcePage: number | null;
  isPendingReview: boolean;
  isActive: boolean;
  createdAt: string;
  /** Client-only working copies for the always-editable cells — see
   * nd-admin-dictionary.component.ts's DictionaryEntry for the same pattern. */
  draftTermA: string;
  draftTermB: string;
};

type SortColumn = 'termA' | 'termB' | 'status';

/** Admin view/curation of the query-expansion synonym dictionary — see
 * docs/roadmap/QUERY-EXPANSION-PLAN.md. Rows are either admin-added/seeded (Source "manual",
 * active immediately) or suggested by the embedding-similarity harvester from a document
 * (Source "auto", inactive until reviewed — see NdSynonymEntry's doc comment). Mirrors
 * nd-admin-dictionary.component.ts's table/search/sort/bulk-action pattern, plus a "pending
 * review" filter for the auto-suggested rows. */
@Component({
  selector: 'app-nd-admin-synonyms',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './nd-admin-synonyms.component.html',
  styleUrls: ['./nd-admin-dictionary.component.scss', '../nd-shared.scss'],
})
export class NdAdminSynonymsComponent implements OnInit {
  private readonly api = inject(NdApiService);
  readonly auth = inject(NdAuthService);

  entries: SynonymEntry[] = [];
  loading = true;
  error = '';

  termA = '';
  termB = '';
  creating = false;

  savingId: string | null = null;
  deletingId: string | null = null;

  selectedIds = new Set<string>();
  bulkWorking = false;

  searchQuery = '';
  statusFilter = '';
  reviewFilter = '';
  sortColumn: SortColumn = 'termA';
  sortDir: SortDir = 'asc';

  async ngOnInit(): Promise<void> {
    await this.auth.refreshProfile();
    await this.load();
  }

  get isSuperAdmin(): boolean {
    return this.auth.getRole() === 'super_admin';
  }

  async load(silent = false): Promise<void> {
    if (!silent) this.loading = true;
    this.error = '';
    const res = await this.api.getSynonymEntries();
    if (res.success && res.data) {
      this.entries = (res.data as SynonymEntry[]).map((e) => ({
        ...e,
        draftTermA: e.termA,
        draftTermB: e.termB,
      }));
    } else if (!silent || this.entries.length === 0) {
      this.error = res.message ?? 'Failed to load the synonym dictionary';
    }
    this.loading = false;
  }

  get pendingReviewCount(): number {
    return this.entries.filter((e) => e.isPendingReview).length;
  }

  get visibleEntries(): SynonymEntry[] {
    const list = this.entries.filter((e) => {
      if (!matchesSearch(this.searchQuery, [e.termA, e.termB, e.sourceDocumentName])) return false;
      if (this.statusFilter === 'active' && !e.isActive) return false;
      if (this.statusFilter === 'inactive' && e.isActive) return false;
      if (this.reviewFilter === 'pending' && !e.isPendingReview) return false;
      if (this.reviewFilter === 'reviewed' && e.isPendingReview) return false;
      return true;
    });

    return [...list].sort((a, b) => {
      switch (this.sortColumn) {
        case 'termB':
          return compareText(a.termB, b.termB, this.sortDir);
        case 'status':
          return compareText(a.isActive ? 'active' : 'inactive', b.isActive ? 'active' : 'inactive', this.sortDir);
        case 'termA':
        default:
          return compareText(a.termA, b.termA, this.sortDir);
      }
    });
  }

  get hasActiveFilters(): boolean {
    return hasListFilters(this.searchQuery, this.statusFilter, this.reviewFilter);
  }

  toggleSort(column: SortColumn): void {
    const next = nextSortState(this.sortColumn, column, this.sortDir, 'termA');
    this.sortColumn = next.column;
    this.sortDir = next.dir;
  }

  sortMark(column: SortColumn): string {
    return sortIndicator(this.sortColumn, column, this.sortDir);
  }

  clearFilters(): void {
    this.searchQuery = '';
    this.statusFilter = '';
    this.reviewFilter = '';
  }

  isSelected(id: string): boolean {
    return this.selectedIds.has(id);
  }

  toggleSelect(id: string): void {
    if (this.selectedIds.has(id)) this.selectedIds.delete(id);
    else this.selectedIds.add(id);
  }

  get allVisibleSelected(): boolean {
    const visible = this.visibleEntries;
    return visible.length > 0 && visible.every((e) => this.selectedIds.has(e.id));
  }

  get someVisibleSelected(): boolean {
    return this.visibleEntries.some((e) => this.selectedIds.has(e.id)) && !this.allVisibleSelected;
  }

  toggleSelectAll(): void {
    const visible = this.visibleEntries;
    if (this.allVisibleSelected) {
      for (const e of visible) this.selectedIds.delete(e.id);
    } else {
      for (const e of visible) this.selectedIds.add(e.id);
    }
  }

  clearSelection(): void {
    this.selectedIds.clear();
  }

  get selectedCount(): number {
    return this.selectedIds.size;
  }

  private get selectedEntries(): SynonymEntry[] {
    return this.entries.filter((e) => this.selectedIds.has(e.id));
  }

  async bulkSetActive(active: boolean): Promise<void> {
    const targets = this.selectedEntries;
    if (!targets.length) return;
    this.bulkWorking = true;
    this.error = '';
    const results = await Promise.all(
      targets.map((e) => this.api.updateSynonymEntry(e.id, { isActive: active })),
    );
    results.forEach((res, i) => {
      if (res.success) targets[i].isActive = active;
    });
    const failed = results.filter((r) => !r.success).length;
    if (failed) this.error = `${failed} of ${targets.length} entries failed to update.`;
    this.bulkWorking = false;
  }

  async bulkDelete(): Promise<void> {
    const targets = this.selectedEntries;
    if (!targets.length) return;
    if (!confirm(`Delete ${targets.length} selected ${targets.length === 1 ? 'entry' : 'entries'}?`)) return;
    this.bulkWorking = true;
    this.error = '';
    const results = await Promise.all(targets.map((e) => this.api.deleteSynonymEntry(e.id)));
    const deletedIds = new Set(targets.filter((_, i) => results[i].success).map((e) => e.id));
    this.entries = this.entries.filter((e) => !deletedIds.has(e.id));
    for (const id of deletedIds) this.selectedIds.delete(id);
    const failed = results.filter((r) => !r.success).length;
    if (failed) this.error = `${failed} of ${targets.length} entries failed to delete.`;
    this.bulkWorking = false;
  }

  async handleCreate(): Promise<void> {
    if (!this.termA.trim() || !this.termB.trim()) return;
    this.creating = true;
    this.error = '';
    const res = await this.api.createSynonymEntry({
      termA: this.termA.trim(),
      termB: this.termB.trim(),
    });
    if (res.success) {
      this.termA = '';
      this.termB = '';
      await this.load(true);
    } else {
      this.error = res.message ?? 'Failed to add entry';
    }
    this.creating = false;
  }

  isDirty(entry: SynonymEntry): boolean {
    return entry.draftTermA.trim() !== entry.termA || entry.draftTermB.trim() !== entry.termB;
  }

  async saveRow(entry: SynonymEntry): Promise<void> {
    if (!entry.draftTermA.trim() || !entry.draftTermB.trim()) {
      this.error = 'Both terms are required.';
      return;
    }
    this.savingId = entry.id;
    this.error = '';
    const termA = entry.draftTermA.trim();
    const termB = entry.draftTermB.trim();
    const res = await this.api.updateSynonymEntry(entry.id, { termA, termB });
    if (res.success) {
      entry.termA = termA;
      entry.termB = termB;
    } else {
      this.error = res.message ?? 'Failed to save';
    }
    this.savingId = null;
  }

  revertRow(entry: SynonymEntry): void {
    entry.draftTermA = entry.termA;
    entry.draftTermB = entry.termB;
  }

  async toggleActive(entry: SynonymEntry): Promise<void> {
    this.savingId = entry.id;
    this.error = '';
    const wasActive = entry.isActive;
    entry.isActive = !wasActive;
    entry.isPendingReview = entry.source === 'auto' && !entry.isActive;
    const res = await this.api.updateSynonymEntry(entry.id, { isActive: entry.isActive });
    if (!res.success) {
      entry.isActive = wasActive;
      entry.isPendingReview = entry.source === 'auto' && !entry.isActive;
      this.error = res.message ?? 'Failed to update';
    }
    this.savingId = null;
  }

  async handleDelete(id: string): Promise<void> {
    if (!confirm('Delete this synonym entry?')) return;
    this.deletingId = id;
    this.error = '';
    const res = await this.api.deleteSynonymEntry(id);
    if (!res.success) {
      this.error = res.message ?? 'Failed to delete';
    } else {
      this.entries = this.entries.filter((e) => e.id !== id);
      this.selectedIds.delete(id);
    }
    this.deletingId = null;
  }

  downloadCsv(): void {
    const header = ['Term A', 'Term B', 'Source', 'Source document', 'Active', 'Created'];
    const escape = (v: string) => `"${v.replace(/"/g, '""')}"`;
    const rows = this.visibleEntries.map((e) =>
      [e.termA, e.termB, e.source, e.sourceDocumentName ?? '', e.isActive ? 'yes' : 'no', e.createdAt]
        .map(escape)
        .join(','),
    );
    const csv = [header.map(escape).join(','), ...rows].join('\n');
    const blob = new Blob([csv], { type: 'text/csv;charset=utf-8;' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = 'synonyms.csv';
    a.click();
    URL.revokeObjectURL(url);
  }
}
