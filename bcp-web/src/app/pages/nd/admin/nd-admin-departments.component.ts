import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { NdApiService } from '../../../services/nd/nd-api.service';
import { NdAuthService } from '../../../services/nd/nd-auth.service';
import { ToastService } from '../../../services/toast.service';
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

/** Departments nest at most this deep, e.g. Compliance › AML/CFT › Sanctions Screening. */
const MAX_LEVEL = 3;

type DeptNode = { dept: Department; level: number; children: DeptNode[]; descendantCount: number };

/** Where the add/edit form is open: a new top-level department, a new child of `parentId`, or editing `id`. */
type DeptForm = { mode: 'add-root' } | { mode: 'add-child'; parentId: string } | { mode: 'edit'; id: string };

@Component({
  selector: 'app-nd-admin-departments',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './nd-admin-departments.component.html',
  styleUrls: ['../nd-shared.scss', './nd-admin-rulebook-categories.component.scss'],
})
export class NdAdminDepartmentsComponent implements OnInit {
  private readonly api = inject(NdApiService);
  private readonly toast = inject(ToastService);
  readonly auth = inject(NdAuthService);
  readonly maxLevel = MAX_LEVEL;

  // ---- tree (real accounts; demo accounts keep the flat list below) ----
  roots: DeptNode[] = [];
  private nodesById = new Map<string, DeptNode>();
  expanded = new Set<string>();
  private expandedOnce = false;
  treeSearch = '';
  form: DeptForm | null = null;
  formName = '';
  formDescription = '';
  saving = false;
  importing = false;

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
      this.buildTree();
      if (!this.expandedOnce && this.departments.length) {
        this.expandAll();
        this.expandedOnce = true;
      }
    } else if (!silent || this.departments.length === 0) {
      this.error = res.message ?? 'Failed to load departments';
    }
    this.loading = false;
  }

  /** Real accounts manage departments as a tree; demo accounts keep the original flat page. */
  get treeMode(): boolean {
    return !this.auth.isDemoViewer();
  }

  private buildTree(): void {
    const byParent = new Map<string | null, Department[]>();
    for (const d of this.departments) {
      const key = d.parentId ?? null;
      byParent.set(key, [...(byParent.get(key) ?? []), d]);
    }
    this.nodesById.clear();
    const build = (parentId: string | null, level: number): DeptNode[] =>
      (byParent.get(parentId) ?? []).sort((a, b) => a.name.localeCompare(b.name)).map((dept) => {
        const children = level < MAX_LEVEL ? build(dept.id, level + 1) : [];
        const node: DeptNode = {
          dept,
          level,
          children,
          descendantCount: children.reduce((n, c) => n + 1 + c.descendantCount, 0),
        };
        this.nodesById.set(dept.id, node);
        return node;
      });
    this.roots = build(null, 1);
  }

  get treeRows(): Array<{ node: DeptNode; hasChildren: boolean }> {
    const q = this.treeSearch.trim().toLowerCase();
    const rows: Array<{ node: DeptNode; hasChildren: boolean }> = [];
    const matches = (n: DeptNode): boolean =>
      n.dept.name.toLowerCase().includes(q) || (n.dept.description ?? '').toLowerCase().includes(q) || n.children.some(matches);
    const walk = (nodes: DeptNode[]) => {
      for (const node of nodes) {
        if (q && !matches(node)) continue;
        rows.push({ node, hasChildren: node.children.length > 0 });
        if (q || this.expanded.has(node.dept.id)) walk(node.children);
      }
    };
    walk(this.roots);
    return rows;
  }

  isExpanded(node: DeptNode): boolean {
    return !!this.treeSearch.trim() || this.expanded.has(node.dept.id);
  }

  toggle(node: DeptNode): void {
    if (this.expanded.has(node.dept.id)) this.expanded.delete(node.dept.id);
    else this.expanded.add(node.dept.id);
  }

  expandAll(): void {
    for (const node of this.nodesById.values()) if (node.children.length) this.expanded.add(node.dept.id);
  }

  collapseAll(): void {
    this.expanded.clear();
  }

  levelLabel(level: number): string {
    return level === 1 ? 'Department' : level === 2 ? 'Unit' : 'Team';
  }

  openAddRoot(): void {
    this.openForm({ mode: 'add-root' }, '', '');
  }

  openAddChild(node: DeptNode): void {
    this.expanded.add(node.dept.id);
    this.openForm({ mode: 'add-child', parentId: node.dept.id }, '', '');
  }

  openEdit(node: DeptNode): void {
    this.openForm({ mode: 'edit', id: node.dept.id }, node.dept.name, node.dept.description ?? '');
  }

  private openForm(target: DeptForm, name: string, description: string): void {
    this.form = target;
    this.formName = name;
    this.formDescription = description;
    this.error = '';
  }

  cancelForm(): void {
    this.form = null;
  }

  isAddingChildOf(node: DeptNode): boolean {
    return this.form?.mode === 'add-child' && this.form.parentId === node.dept.id;
  }

  isEditing(node: DeptNode): boolean {
    return this.form?.mode === 'edit' && this.form.id === node.dept.id;
  }

  async saveForm(): Promise<void> {
    const form = this.form;
    const name = this.formName.trim();
    if (!form || !name || this.saving) return;
    this.saving = true;
    this.error = '';
    const description = this.formDescription.trim() || undefined;
    let res;
    if (form.mode === 'edit') {
      const dept = this.departments.find((d) => d.id === form.id);
      res = await this.api.updateDepartment(form.id, { name, description, isActive: dept?.isActive ?? true });
    } else {
      res = await this.api.createDepartment({
        name,
        description,
        parentId: form.mode === 'add-child' ? form.parentId : null,
      });
    }
    this.saving = false;
    if (!res.success) {
      this.error = res.message ?? 'Could not save the department';
      return;
    }
    this.toast.show(form.mode === 'edit' ? 'Department updated' : 'Department added', 'success', 2500);
    this.form = null;
    await this.load(true);
  }

  async removeNode(node: DeptNode): Promise<void> {
    const below = node.descendantCount;
    const message = below
      ? `Delete "${node.dept.name}" and the ${below} department${below === 1 ? '' : 's'} under it? This cannot be undone.`
      : `Delete "${node.dept.name}"? This cannot be undone.`;
    if (!confirm(message)) return;
    this.deletingDeptId = node.dept.id;
    this.error = '';
    const res = await this.api.deleteDepartment(node.dept.id);
    this.deletingDeptId = null;
    if (!res.success) {
      this.error = res.message ?? 'Could not delete the department';
      return;
    }
    this.toast.show('Department deleted', 'success', 2500);
    await this.load(true);
  }

  async importSample(): Promise<void> {
    if (this.importing) return;
    this.importing = true;
    this.error = '';
    const res = await this.api.importSampleDepartments();
    this.importing = false;
    if (!res.success) {
      this.error = res.message ?? 'Could not add the sample departments';
      return;
    }
    const imported = (res as { imported?: number }).imported ?? 0;
    this.toast.show(`Added ${imported} sample departments`, 'success', 3500);
    await this.load(true);
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
