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

export type DictionaryEntry = {
  id: string;
  acronym: string;
  definition: string;
  isUnresolved: boolean;
  source: 'auto' | 'manual';
  sourceDocumentId: string | null;
  sourceDocumentName: string | null;
  sourcePage: number | null;
  isActive: boolean;
  createdAt: string;
  /** Client-only working copies for the always-editable cells — bound directly to the row's
   * inputs (spreadsheet-style, no separate "edit mode"). Compared against the saved value to
   * decide whether a row's Save button should show. */
  draftAcronym: string;
  draftDefinition: string;
};

type SortColumn = 'acronym' | 'definition' | 'source' | 'status';

/** Admin view/curation of the query-expansion acronym dictionary — see
 * docs/roadmap/QUERY-EXPANSION-PLAN.md. Mirrors nd-admin-departments.component.ts's flat-list
 * pattern (search/filter/sort/add/toggle/delete), plus an "unresolved" highlight (a short form
 * seen in a document with no known full form yet) and inline edit of the definition so an admin
 * can fill that gap in, plus a CSV export. */
@Component({
  selector: 'app-nd-admin-dictionary',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './nd-admin-dictionary.component.html',
  styleUrls: ['./nd-admin-dictionary.component.scss', '../nd-shared.scss'],
})
export class NdAdminDictionaryComponent implements OnInit {
  private readonly api = inject(NdApiService);
  readonly auth = inject(NdAuthService);

  entries: DictionaryEntry[] = [];
  loading = true;
  error = '';

  acronym = '';
  definition = '';
  creating = false;

  savingId: string | null = null;
  deletingId: string | null = null;

  selectedIds = new Set<string>();
  bulkWorking = false;

  searchQuery = '';
  sourceFilter = '';
  statusFilter = '';
  resolvedFilter = '';
  sortColumn: SortColumn = 'acronym';
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
    const res = await this.api.getDictionaryEntries();
    if (res.success && res.data) {
      this.entries = (res.data as DictionaryEntry[]).map((e) => ({
        ...e,
        draftAcronym: e.acronym,
        draftDefinition: e.definition,
      }));
    } else if (!silent || this.entries.length === 0) {
      this.error = res.message ?? 'Failed to load the dictionary';
    }
    this.loading = false;
  }

  get visibleEntries(): DictionaryEntry[] {
    const list = this.entries.filter((e) => {
      if (!matchesSearch(this.searchQuery, [e.acronym, e.definition, e.sourceDocumentName])) return false;
      if (this.sourceFilter && e.source !== this.sourceFilter) return false;
      if (this.statusFilter === 'active' && !e.isActive) return false;
      if (this.statusFilter === 'inactive' && e.isActive) return false;
      if (this.resolvedFilter === 'unresolved' && !e.isUnresolved) return false;
      if (this.resolvedFilter === 'resolved' && e.isUnresolved) return false;
      return true;
    });

    return [...list].sort((a, b) => {
      switch (this.sortColumn) {
        case 'definition':
          return compareText(a.definition, b.definition, this.sortDir);
        case 'source':
          return compareText(a.source, b.source, this.sortDir);
        case 'status':
          return compareText(a.isActive ? 'active' : 'inactive', b.isActive ? 'active' : 'inactive', this.sortDir);
        case 'acronym':
        default:
          return compareText(a.acronym, b.acronym, this.sortDir);
      }
    });
  }

  get unresolvedCount(): number {
    return this.entries.filter((e) => e.isUnresolved).length;
  }

  get hasActiveFilters(): boolean {
    return hasListFilters(this.searchQuery, this.sourceFilter, this.statusFilter, this.resolvedFilter);
  }

  toggleSort(column: SortColumn): void {
    const next = nextSortState(this.sortColumn, column, this.sortDir, 'acronym');
    this.sortColumn = next.column;
    this.sortDir = next.dir;
  }

  sortMark(column: SortColumn): string {
    return sortIndicator(this.sortColumn, column, this.sortDir);
  }

  clearFilters(): void {
    this.searchQuery = '';
    this.sourceFilter = '';
    this.statusFilter = '';
    this.resolvedFilter = '';
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

  private get selectedEntries(): DictionaryEntry[] {
    return this.entries.filter((e) => this.selectedIds.has(e.id));
  }

  async bulkSetActive(active: boolean): Promise<void> {
    const targets = this.selectedEntries;
    if (!targets.length) return;
    this.bulkWorking = true;
    this.error = '';
    const results = await Promise.all(
      targets.map((e) => this.api.updateDictionaryEntry(e.id, { isActive: active })),
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
    const results = await Promise.all(targets.map((e) => this.api.deleteDictionaryEntry(e.id)));
    const deletedIds = new Set(targets.filter((_, i) => results[i].success).map((e) => e.id));
    this.entries = this.entries.filter((e) => !deletedIds.has(e.id));
    for (const id of deletedIds) this.selectedIds.delete(id);
    const failed = results.filter((r) => !r.success).length;
    if (failed) this.error = `${failed} of ${targets.length} entries failed to delete.`;
    this.bulkWorking = false;
  }

  async handleCreate(): Promise<void> {
    if (!this.acronym.trim()) return;
    this.creating = true;
    this.error = '';
    const res = await this.api.createDictionaryEntry({
      acronym: this.acronym.trim(),
      definition: this.definition.trim(),
    });
    if (res.success) {
      this.acronym = '';
      this.definition = '';
      await this.load(true);
    } else {
      this.error = res.message ?? 'Failed to add entry';
    }
    this.creating = false;
  }

  isDirty(entry: DictionaryEntry): boolean {
    return entry.draftAcronym.trim() !== entry.acronym || entry.draftDefinition.trim() !== entry.definition;
  }

  async saveRow(entry: DictionaryEntry): Promise<void> {
    if (!entry.draftAcronym.trim()) {
      this.error = 'Acronym cannot be empty.';
      return;
    }
    this.savingId = entry.id;
    this.error = '';
    const acronym = entry.draftAcronym.trim();
    const definition = entry.draftDefinition.trim();
    const res = await this.api.updateDictionaryEntry(entry.id, { acronym, definition });
    if (res.success) {
      entry.acronym = acronym;
      entry.definition = definition;
      entry.isUnresolved = definition.length === 0;
    } else {
      this.error = res.message ?? 'Failed to save';
    }
    this.savingId = null;
  }

  revertRow(entry: DictionaryEntry): void {
    entry.draftAcronym = entry.acronym;
    entry.draftDefinition = entry.definition;
  }

  async toggleActive(entry: DictionaryEntry): Promise<void> {
    this.savingId = entry.id;
    this.error = '';
    const wasActive = entry.isActive;
    entry.isActive = !wasActive;
    const res = await this.api.updateDictionaryEntry(entry.id, { isActive: entry.isActive });
    if (!res.success) {
      entry.isActive = wasActive;
      this.error = res.message ?? 'Failed to update';
    }
    this.savingId = null;
  }

  async handleDelete(id: string): Promise<void> {
    if (!confirm('Delete this dictionary entry?')) return;
    this.deletingId = id;
    this.error = '';
    const res = await this.api.deleteDictionaryEntry(id);
    if (!res.success) {
      this.error = res.message ?? 'Failed to delete';
    } else {
      this.entries = this.entries.filter((e) => e.id !== id);
      this.selectedIds.delete(id);
    }
    this.deletingId = null;
  }

  downloadCsv(): void {
    const header = ['Acronym', 'Definition', 'Source', 'Source document', 'Page', 'Active', 'Created'];
    const escape = (v: string) => `"${v.replace(/"/g, '""')}"`;
    const rows = this.visibleEntries.map((e) =>
      [
        e.acronym,
        e.definition,
        e.source,
        e.sourceDocumentName ?? '',
        e.sourcePage != null ? String(e.sourcePage) : '',
        e.isActive ? 'yes' : 'no',
        e.createdAt,
      ]
        .map(escape)
        .join(','),
    );
    const csv = [header.map(escape).join(','), ...rows].join('\n');
    const blob = new Blob([csv], { type: 'text/csv;charset=utf-8;' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = 'dictionary.csv';
    a.click();
    URL.revokeObjectURL(url);
  }
}
