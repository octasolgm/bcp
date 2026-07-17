import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { NdStatusBadgeComponent } from '../../../components/nd/nd-status-badge.component';
import { NdApiService } from '../../../services/nd/nd-api.service';
import { NdAuthService } from '../../../services/nd/nd-auth.service';
import {
  compareNumber,
  compareText,
  hasListFilters,
  matchesSearch,
  nextSortState,
  sortIndicator,
  type SortDir,
} from '../../../../lib/nd/list-utils';
import type { Department } from '../../../../lib/nd/types';

type DeptSortColumn = 'name' | 'docs' | 'libraries' | 'status';

@Component({
  selector: 'app-nd-admin-departments',
  standalone: true,
  imports: [CommonModule, FormsModule, NdStatusBadgeComponent],
  templateUrl: './nd-admin-departments.component.html',
  styleUrls: ['../nd-shared.scss'],
})
export class NdAdminDepartmentsComponent implements OnInit {
  private readonly api = inject(NdApiService);
  readonly auth = inject(NdAuthService);

  departments: Department[] = [];
  name = '';
  description = '';
  loading = true;
  savingDeptId: string | null = null;
  deletingDeptId: string | null = null;
  creating = false;
  error = '';
  searchQuery = '';
  statusFilter = '';
  sortColumn: DeptSortColumn = 'name';
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
    const res = await this.api.getDepartments();
    if (res.success && res.data) {
      this.departments = res.data as Department[];
    } else if (!silent || this.departments.length === 0) {
      this.error = res.message ?? 'Failed to load departments';
    }
    this.loading = false;
  }

  get visibleDepartments(): Department[] {
    let list = this.departments.filter((d) => {
      if (!matchesSearch(this.searchQuery, [d.name, d.description])) return false;
      if (this.statusFilter === 'active' && !d.isActive) return false;
      if (this.statusFilter === 'inactive' && d.isActive) return false;
      return true;
    });

    return [...list].sort((a, b) => {
      switch (this.sortColumn) {
        case 'docs':
          return compareNumber(a.documentCount ?? 0, b.documentCount ?? 0, this.sortDir);
        case 'libraries':
          return compareNumber(a.libraryCount ?? 0, b.libraryCount ?? 0, this.sortDir);
        case 'status':
          return compareNumber(a.isActive ? 1 : 0, b.isActive ? 1 : 0, this.sortDir);
        case 'name':
        default:
          return compareText(a.name, b.name, this.sortDir);
      }
    });
  }

  get hasActiveFilters(): boolean {
    return hasListFilters(this.searchQuery, this.statusFilter);
  }

  toggleSort(column: DeptSortColumn): void {
    const next = nextSortState(this.sortColumn, column, this.sortDir, 'name');
    this.sortColumn = next.column;
    this.sortDir = next.dir;
  }

  sortMark(column: DeptSortColumn): string {
    return sortIndicator(this.sortColumn, column, this.sortDir);
  }

  clearFilters(): void {
    this.searchQuery = '';
    this.statusFilter = '';
  }

  async handleCreate(): Promise<void> {
    this.creating = true;
    this.error = '';
    const res = await this.api.createDepartment({
      name: this.name.trim(),
      description: this.description.trim(),
    });
    if (res.success) {
      this.name = '';
      this.description = '';
      await this.load(true);
    } else {
      this.error = res.message ?? 'Failed to create';
    }
    this.creating = false;
  }

  async toggleActive(dept: Department): Promise<void> {
    this.savingDeptId = dept.id;
    this.error = '';
    const wasActive = dept.isActive;
    dept.isActive = !wasActive;

    const res = await this.api.updateDepartment(dept.id, {
      name: dept.name,
      description: dept.description ?? undefined,
      isActive: dept.isActive,
    });
    if (!res.success) {
      dept.isActive = wasActive;
      this.error = res.message ?? 'Failed to update department';
    }
    this.savingDeptId = null;
  }

  async handleDelete(id: string): Promise<void> {
    if (!confirm('Delete department?')) return;
    this.deletingDeptId = id;
    this.error = '';
    const res = await this.api.deleteDepartment(id);
    if (!res.success) {
      this.error = res.message ?? 'Failed to delete';
    } else {
      this.departments = this.departments.filter((d) => d.id !== id);
    }
    this.deletingDeptId = null;
  }
}
