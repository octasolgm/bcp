import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { NdApiService } from '../../../services/nd/nd-api.service';
import { NdAuthService } from '../../../services/nd/nd-auth.service';
import { ToastService } from '../../../services/toast.service';
import type { RulebookCategory } from '../../../../lib/nd/types';

/** Authority › category › sub-category › rulebook, e.g. Central Bank of the UAE › Banking › Capital Adequacy ›
 * Regulations Re Capital Adequacy. */
const MAX_LEVEL = 4;

type CategoryNode = {
  category: RulebookCategory;
  level: number;
  children: CategoryNode[];
  /** Everything below this node, at any level. */
  descendantCount: number;
};

type VisibleRow = { node: CategoryNode; hasChildren: boolean };

/** Where the add/edit form is open: a new top-level category, a new child of `parentId`, or editing `id`. */
type FormTarget = { mode: 'add-root' } | { mode: 'add-child'; parentId: string } | { mode: 'edit'; id: string };

@Component({
  selector: 'app-nd-admin-rulebook-categories',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  templateUrl: './nd-admin-rulebook-categories.component.html',
  styleUrls: ['../nd-shared.scss', './nd-admin-rulebook-categories.component.scss'],
})
export class NdAdminRulebookCategoriesComponent implements OnInit {
  private readonly api = inject(NdApiService);
  private readonly toast = inject(ToastService);
  readonly auth = inject(NdAuthService);
  readonly maxLevel = MAX_LEVEL;

  categories: RulebookCategory[] = [];
  roots: CategoryNode[] = [];
  private nodesById = new Map<string, CategoryNode>();
  expanded = new Set<string>();

  loading = true;
  error = '';
  searchQuery = '';
  /** all | with-doc (rulebooks that have a document) | without-doc (rulebooks still missing one). */
  docFilter: '' | 'with-doc' | 'without-doc' = '';
  private expandedOnce = false;
  /** "Upload downloaded PDFs" progress and the files it could not match to a rulebook. */
  bulkUpload: { done: number; total: number } | null = null;
  unmatchedFiles: string[] = [];
  importing = false;
  saving = false;
  deletingId: string | null = null;

  /** "Import rulebook documents" progress; null when not running. */
  docImport: { done: number; total: number; added: number; failed: number } | null = null;
  private stopDocImport = false;
  /** Shown when the source site refuses automatic downloads. */
  docImportNotice = '';
  uploadingId: string | null = null;
  /** Categories whose document list is open, and the documents filed under each (loaded on first open). */
  docsOpen = new Set<string>();
  private docsByCategory = new Map<string, Array<{ id: string; name: string }>>();
  docsLoading = false;
  /** Banner while a single upload runs: "Uploading <file> to <category>…". */
  uploadNotice = '';

  form: FormTarget | null = null;
  formName = '';
  formDescription = '';

  async ngOnInit(): Promise<void> {
    await this.auth.refreshProfile();
    await this.load();
  }

  get isAdmin(): boolean {
    return this.auth.getRole() === 'super_admin';
  }

  get totalCount(): number {
    return this.categories.length;
  }

  async load(silent = false): Promise<void> {
    if (!silent) this.loading = true;
    this.error = '';
    const res = await this.api.getRulebookCategories();
    if (res.success && res.data) {
      this.categories = res.data;
      this.buildTree();
      // Open every level the first time, so all rulebooks are visible straight away.
      if (!this.expandedOnce && this.categories.length) {
        this.expandAll();
        this.expandedOnce = true;
      }
    } else if (!silent || this.categories.length === 0) {
      this.error = res.message ?? 'Failed to load rulebook categories';
    }
    this.loading = false;
  }

  private buildTree(): void {
    const byParent = new Map<string | null, RulebookCategory[]>();
    for (const c of this.categories) {
      const key = c.parentId ?? null;
      byParent.set(key, [...(byParent.get(key) ?? []), c]);
    }
    const order = (a: RulebookCategory, b: RulebookCategory) =>
      a.sortOrder - b.sortOrder || a.name.localeCompare(b.name);
    this.nodesById.clear();
    const build = (parentId: string | null, level: number): CategoryNode[] =>
      (byParent.get(parentId) ?? []).sort(order).map((category) => {
        const children = level < MAX_LEVEL ? build(category.id, level + 1) : [];
        const node: CategoryNode = {
          category,
          level,
          children,
          descendantCount: children.reduce((n, c) => n + 1 + c.descendantCount, 0),
        };
        this.nodesById.set(category.id, node);
        return node;
      });
    this.roots = build(null, 1);
  }

  /** Rows in display order. A search shows matches with their parents (opened), otherwise follows what is expanded. */
  get visibleRows(): VisibleRow[] {
    const q = this.searchQuery.trim().toLowerCase();
    const filtering = !!q || !!this.docFilter;
    const rows: VisibleRow[] = [];
    const selfMatches = (n: CategoryNode): boolean => {
      if (q && !n.category.name.toLowerCase().includes(q) && !(n.category.description ?? '').toLowerCase().includes(q)) {
        return false;
      }
      if (this.docFilter === 'with-doc') return (n.category.documentCount ?? 0) > 0;
      if (this.docFilter === 'without-doc') return !(n.category.documentCount ?? 0);
      return true;
    };
    const matches = (n: CategoryNode): boolean => selfMatches(n) || n.children.some(matches);
    const walk = (nodes: CategoryNode[]) => {
      for (const node of nodes) {
        if (filtering && !matches(node)) continue;
        rows.push({ node, hasChildren: node.children.length > 0 });
        if (filtering || this.expanded.has(node.category.id)) walk(node.children);
      }
    };
    walk(this.roots);
    return rows;
  }

  isExpanded(node: CategoryNode): boolean {
    return !!this.searchQuery.trim() || !!this.docFilter || this.expanded.has(node.category.id);
  }

  /** Rulebooks: the items at the bottom of an authority's tree (third or fourth level, nothing under them). */
  get rulebookCount(): number {
    return [...this.nodesById.values()].filter((n) => n.level >= 3 && !n.children.length).length;
  }

  toggle(node: CategoryNode): void {
    if (this.expanded.has(node.category.id)) this.expanded.delete(node.category.id);
    else this.expanded.add(node.category.id);
  }

  expandAll(): void {
    for (const node of this.nodesById.values()) if (node.children.length) this.expanded.add(node.category.id);
  }

  collapseAll(): void {
    this.expanded.clear();
  }

  levelLabel(level: number, hasChildren = false): string {
    if (level === 1) return 'Authority';
    if (level === 2) return 'Category';
    return level === MAX_LEVEL || (level === 3 && !hasChildren) ? 'Rulebook' : 'Sub-category';
  }

  /** "Add ..." label for the level below this one. */
  childLabel(level: number): string {
    return level === 1 ? 'category' : level === 2 ? 'sub-category' : 'rulebook';
  }

  // ---- add / edit ---------------------------------------------------------------------------------------

  openAddRoot(): void {
    this.openForm({ mode: 'add-root' }, '', '');
  }

  openAddChild(node: CategoryNode): void {
    this.expanded.add(node.category.id);
    this.openForm({ mode: 'add-child', parentId: node.category.id }, '', '');
  }

  openEdit(node: CategoryNode): void {
    this.openForm({ mode: 'edit', id: node.category.id }, node.category.name, node.category.description ?? '');
  }

  private openForm(target: FormTarget, name: string, description: string): void {
    this.form = target;
    this.formName = name;
    this.formDescription = description;
    this.error = '';
  }

  cancelForm(): void {
    this.form = null;
  }

  isAddingChildOf(node: CategoryNode): boolean {
    return this.form?.mode === 'add-child' && this.form.parentId === node.category.id;
  }

  isEditing(node: CategoryNode): boolean {
    return this.form?.mode === 'edit' && this.form.id === node.category.id;
  }

  async saveForm(): Promise<void> {
    const form = this.form;
    const name = this.formName.trim();
    if (!form || !name || this.saving) return;
    this.saving = true;
    this.error = '';
    const description = this.formDescription.trim() || undefined;
    const res =
      form.mode === 'edit'
        ? await this.api.updateRulebookCategory(form.id, { name, description })
        : await this.api.createRulebookCategory({
            name,
            description,
            parentId: form.mode === 'add-child' ? form.parentId : null,
          });
    this.saving = false;
    if (!res.success) {
      this.error = res.message ?? 'Could not save the category';
      return;
    }
    this.toast.show(form.mode === 'edit' ? 'Category updated' : 'Category added', 'success', 2500);
    this.form = null;
    await this.load(true);
  }

  // ---- delete / import ----------------------------------------------------------------------------------

  async remove(node: CategoryNode): Promise<void> {
    const below = node.descendantCount;
    const message = below
      ? `Delete "${node.category.name}" and the ${below} categor${below === 1 ? 'y' : 'ies'} under it? This cannot be undone.`
      : `Delete "${node.category.name}"? This cannot be undone.`;
    if (!confirm(message)) return;
    this.deletingId = node.category.id;
    this.error = '';
    const res = await this.api.deleteRulebookCategory(node.category.id);
    this.deletingId = null;
    if (!res.success) {
      this.error = res.message ?? 'Could not delete the category';
      return;
    }
    this.toast.show('Category deleted', 'success', 2500);
    await this.load(true);
  }

  /** Rulebooks whose document can be downloaded from the source and that have none yet. */
  get pendingDocumentImports(): RulebookCategory[] {
    return this.categories.filter((c) => c.hasDocumentSource && !(c.documentCount ?? 0));
  }

  get importedDocumentCount(): number {
    return this.categories.reduce((n, c) => n + (c.documentCount ?? 0), 0);
  }

  /** One request per rulebook, so the page can show progress and stop part-way. */
  async importDocuments(): Promise<void> {
    const pending = this.pendingDocumentImports;
    if (!pending.length || this.docImport) return;
    this.stopDocImport = false;
    this.docImportNotice = '';
    this.error = '';
    this.docImport = { done: 0, total: pending.length, added: 0, failed: 0 };
    for (const category of pending) {
      if (this.stopDocImport) break;
      const res = await this.api.importRulebookDocument(category.id);
      this.docImport.done++;
      if (res.success) {
        if (res.data) this.docImport.added++;
        continue;
      }
      this.docImport.failed++;
      if ((res.message ?? '').includes('does not allow automatic downloads')) {
        this.docImportNotice = res.message ?? '';
        break;
      }
    }
    const { added, failed } = this.docImport;
    this.docImport = null;
    if (added) this.toast.show(`Imported ${added} rulebook document${added === 1 ? '' : 's'}; parsing has started`, 'success', 4000);
    else if (failed && !this.docImportNotice) this.error = 'No documents could be imported from the source.';
    await this.load(true);
  }

  /**
   * The source site only lets a person's browser download its PDFs. After downloading them (Download PDF on each
   * rulebook), they can all be uploaded here at once: each file is matched to its rulebook by the PDF's file name
   * (e.g. CBUAE_EN_1836_VER1.pdf) and filed under it.
   */
  async uploadDownloadedPdfs(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const files = [...(input.files ?? [])];
    input.value = '';
    if (!files.length || this.bulkUpload) return;
    const byFileName = new Map<string, RulebookCategory>();
    for (const c of this.categories) {
      const name = this.pdfFileName(c.documentUrl);
      if (name && !(c.documentCount ?? 0)) byFileName.set(name, c);
    }
    const key = (fileName: string) => fileName.toLowerCase().replace(/\s*\(\d+\)(?=\.pdf$)/, '');
    const matched: Array<{ file: File; category: RulebookCategory }> = [];
    this.unmatchedFiles = [];
    for (const file of files) {
      const category = byFileName.get(key(file.name)) ?? byFileName.get(file.name.toLowerCase());
      if (category) matched.push({ file, category });
      else this.unmatchedFiles.push(file.name);
    }
    this.error = '';
    this.bulkUpload = { done: 0, total: matched.length };
    let added = 0;
    for (const { file, category } of matched) {
      const res = await this.api.uploadRulebookDocument(category.id, file);
      this.bulkUpload.done++;
      if (res.success) added++;
      else this.unmatchedFiles.push(`${file.name} (${res.message ?? 'upload failed'})`);
    }
    this.bulkUpload = null;
    if (added) this.toast.show(`Added ${added} rulebook document${added === 1 ? '' : 's'}; parsing has started`, 'success', 4000);
    await this.load(true);
  }

  /** "cbuae_en_1836_ver1 (1).pdf" from the document link, lower case, for matching uploads. */
  private pdfFileName(url?: string | null): string | null {
    if (!url) return null;
    try {
      const last = decodeURIComponent(new URL(url).pathname.split('/').pop() ?? '');
      return last ? last.toLowerCase().replace(/\s*\(\d+\)(?=\.pdf$)/, '') : null;
    } catch {
      return null;
    }
  }

  cancelDocImport(): void {
    this.stopDocImport = true;
  }

  async uploadDocument(node: CategoryNode, event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (!file || this.uploadingId) return;
    this.uploadingId = node.category.id;
    this.uploadNotice = `Uploading "${file.name}" to ${node.category.name}…`;
    this.error = '';
    const res = await this.api.uploadRulebookDocument(node.category.id, file);
    if (!res.success) {
      this.uploadingId = null;
      this.uploadNotice = '';
      this.error = res.message ?? 'Could not upload the document';
      return;
    }
    this.uploadNotice = `Added "${file.name}". Refreshing…`;
    // Keep the row busy until the list shows the new document count.
    await this.load(true);
    if (this.docsByCategory.size || this.docsOpen.size) await this.loadCategoryDocs();
    this.uploadingId = null;
    this.uploadNotice = '';
    this.toast.show(`Added "${file.name}" under ${node.category.name}; parsing has started`, 'success', 4000);
  }

  async toggleDocs(node: CategoryNode): Promise<void> {
    const id = node.category.id;
    if (this.docsOpen.has(id)) {
      this.docsOpen.delete(id);
      return;
    }
    this.docsOpen.add(id);
    if (!this.docsByCategory.size) await this.loadCategoryDocs();
  }

  docsFor(node: CategoryNode): Array<{ id: string; name: string }> {
    return this.docsByCategory.get(node.category.id) ?? [];
  }

  /** Regulation documents per category, from the category links and the regulation list (names). */
  private async loadCategoryDocs(): Promise<void> {
    this.docsLoading = true;
    const [links, docs] = await Promise.all([this.api.getRegulationCategoryLinks(), this.api.getRegulationDocuments()]);
    this.docsLoading = false;
    if (!links.success || !links.data) return;
    const names = new Map(
      ((docs.data ?? []) as Array<{ id: string; name: string }>).map((d) => [d.id, d.name]),
    );
    const byCategory = new Map<string, Array<{ id: string; name: string }>>();
    for (const [docId, categoryIds] of Object.entries(links.data)) {
      const name = names.get(docId);
      if (!name) continue; // deleted or not visible
      for (const categoryId of categoryIds) {
        byCategory.set(categoryId, [...(byCategory.get(categoryId) ?? []), { id: docId, name }]);
      }
    }
    for (const list of byCategory.values()) list.sort((a, b) => a.name.localeCompare(b.name));
    this.docsByCategory = byCategory;
  }

  async importSample(): Promise<void> {
    if (this.importing) return;
    this.importing = true;
    this.error = '';
    const res = await this.api.importSampleRulebookCategories();
    this.importing = false;
    if (!res.success) {
      this.error = res.message ?? 'Could not import the sample categories';
      return;
    }
    const imported = (res as { imported?: number }).imported ?? 0;
    this.toast.show(`Imported ${imported} rulebook categories`, 'success', 3500);
    await this.load(true);
  }
}
