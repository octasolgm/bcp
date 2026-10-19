import {
  ChangeDetectionStrategy,
  ChangeDetectorRef,
  Component,
  EventEmitter,
  HostListener,
  Input,
  OnInit,
  Output,
  inject,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import {
  NdApiService,
  type NdCloudImportResult,
  type NdCloudItem,
  type NdCloudProvider,
} from '../../services/nd/nd-api.service';
import { NdAuthService } from '../../services/nd/nd-auth.service';

type Crumb = { id: string | null; name: string };

/** Google Drive's "Shared with me" is a virtual folder; the server understands this id. */
const SHARED_WITH_ME = '__shared__';
const MAX_FILES = 25;

/**
 * Pick files from a connected cloud account and copy them into the workspace as Internal or Regulation documents
 * (same record and "Pending parse" status as an upload). Read-only: nothing is changed in the user's account.
 */
@Component({
  selector: 'app-nd-cloud-import-dialog',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  templateUrl: './nd-cloud-import-dialog.component.html',
  styleUrl: './nd-cloud-import-dialog.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class NdCloudImportDialogComponent implements OnInit {
  private readonly api = inject(NdApiService);
  private readonly cdr = inject(ChangeDetectorRef);
  readonly auth = inject(NdAuthService);

  /** Which kind of document the picked files become. */
  @Input() kind: 'internal' | 'regulation' = 'internal';
  @Input() departmentId: string | null = null;

  @Output() closed = new EventEmitter<void>();
  /** Number of files that were actually added, so the page can refresh its list. */
  @Output() imported = new EventEmitter<number>();

  providers: NdCloudProvider[] = [];
  active: NdCloudProvider | null = null;
  loadingProviders = true;

  trail: Crumb[] = [{ id: null, name: 'My files' }];
  items: NdCloudItem[] = [];
  nextPageToken: string | null = null;
  folderName = '';
  search = '';
  searching = false;
  loadingList = false;
  error = '';
  needsReconnect = false;

  selected = new Map<string, NdCloudItem>();
  importing = false;
  results: NdCloudImportResult[] | null = null;
  importedCount = 0;

  @HostListener('document:keydown.escape')
  onEscape(): void {
    if (!this.importing) this.close();
  }

  async ngOnInit(): Promise<void> {
    const res = await this.api.getCloudProviders();
    this.providers = (res.success && res.data ? res.data : []).filter((p) => !!p.connection);
    this.loadingProviders = false;
    if (!res.success) this.error = res.message ?? 'Could not load your connected storage.';
    if (this.providers.length === 1) await this.choose(this.providers[0]);
    this.cdr.markForCheck();
  }

  get kindLabel(): string {
    return this.kind === 'regulation' ? 'Regulation documents' : 'Internal documents';
  }

  get canManageConnections(): boolean {
    return this.auth.getRole() === 'super_admin';
  }

  get isGoogle(): boolean {
    return this.active?.id === 'google';
  }

  get selectedCount(): number {
    return this.selected.size;
  }

  async choose(provider: NdCloudProvider): Promise<void> {
    this.active = provider;
    this.trail = [{ id: null, name: this.isGoogle ? 'My Drive' : 'My files' }];
    this.selected.clear();
    this.results = null;
    this.search = '';
    this.searching = false;
    await this.load();
  }

  backToProviders(): void {
    this.active = null;
    this.items = [];
    this.selected.clear();
    this.error = '';
    this.needsReconnect = false;
  }

  private get currentFolder(): string | null {
    return this.trail[this.trail.length - 1].id;
  }

  async load(append = false): Promise<void> {
    if (!this.active?.connection) return;
    this.loadingList = true;
    this.error = '';
    this.needsReconnect = false;
    this.cdr.markForCheck();
    const res = await this.api.browseCloud(this.active.connection.id, {
      folder: this.searching ? null : this.currentFolder,
      pageToken: append ? this.nextPageToken : null,
      q: this.searching ? this.search.trim() : null,
    });
    this.loadingList = false;
    if (res.success && res.data) {
      this.items = append ? [...this.items, ...res.data.items] : res.data.items;
      this.nextPageToken = res.data.nextPageToken ?? null;
      this.folderName = res.data.folderName;
    } else {
      this.needsReconnect = res.status === 409;
      this.error = res.message ?? 'Could not open this folder.';
    }
    this.cdr.markForCheck();
  }

  async openFolder(item: NdCloudItem): Promise<void> {
    this.searching = false;
    this.search = '';
    this.trail = [...this.trail, { id: item.id, name: item.name }];
    await this.load();
  }

  async goToCrumb(index: number): Promise<void> {
    this.searching = false;
    this.search = '';
    this.trail = this.trail.slice(0, index + 1);
    await this.load();
  }

  async openSharedWithMe(): Promise<void> {
    this.searching = false;
    this.search = '';
    this.trail = [{ id: null, name: 'My Drive' }, { id: SHARED_WITH_ME, name: 'Shared with me' }];
    await this.load();
  }

  async runSearch(): Promise<void> {
    const term = this.search.trim();
    if (!term) {
      await this.clearSearch();
      return;
    }
    this.searching = true;
    await this.load();
  }

  async clearSearch(): Promise<void> {
    this.search = '';
    this.searching = false;
    await this.load();
  }

  isSelected(item: NdCloudItem): boolean {
    return this.selected.has(item.id);
  }

  toggle(item: NdCloudItem): void {
    if (!item.importable || this.importing) return;
    if (this.selected.has(item.id)) this.selected.delete(item.id);
    else if (this.selected.size < MAX_FILES) this.selected.set(item.id, item);
    this.cdr.markForCheck();
  }

  get importableOnPage(): NdCloudItem[] {
    return this.items.filter((i) => i.importable);
  }

  get allOnPageSelected(): boolean {
    const list = this.importableOnPage;
    return list.length > 0 && list.every((i) => this.selected.has(i.id));
  }

  toggleAllOnPage(checked: boolean): void {
    for (const item of this.importableOnPage) {
      if (checked && this.selected.size < MAX_FILES) this.selected.set(item.id, item);
      else if (!checked) this.selected.delete(item.id);
    }
    this.cdr.markForCheck();
  }

  get atLimit(): boolean {
    return this.selected.size >= MAX_FILES;
  }

  readonly maxFiles = MAX_FILES;

  async runImport(): Promise<void> {
    if (!this.active?.connection || this.selected.size === 0 || this.importing) return;
    this.importing = true;
    this.error = '';
    this.cdr.markForCheck();
    const res = await this.api.importFromCloud(this.active.connection.id, {
      fileIds: [...this.selected.keys()],
      kind: this.kind,
      departmentId: this.departmentId,
    });
    this.importing = false;
    if (res.success && res.data) {
      this.results = res.data;
      this.importedCount = res.data.filter((r) => r.result === 'imported').length;
      this.selected.clear();
      if (this.importedCount > 0) this.imported.emit(this.importedCount);
    } else {
      this.needsReconnect = res.status === 409;
      this.error = res.message ?? 'The import did not finish.';
    }
    this.cdr.markForCheck();
  }

  resultLabel(r: NdCloudImportResult): string {
    if (r.result === 'imported') return 'Imported';
    if (r.result === 'duplicate') return 'Already in your documents';
    return r.error || 'Failed';
  }

  anotherImport(): void {
    this.results = null;
    this.cdr.markForCheck();
  }

  sizeLabel(bytes?: number | null): string {
    if (bytes == null) return '';
    if (bytes < 1024 * 1024) return `${Math.max(1, Math.round(bytes / 1024))} KB`;
    return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  }

  typeLabel(item: NdCloudItem): string {
    if (item.isFolder) return 'Folder';
    const m = item.mimeType ?? '';
    if (m === 'application/pdf') return 'PDF';
    if (m.includes('wordprocessingml') || m === 'application/msword') return 'Word';
    if (m === 'application/vnd.google-apps.document') return 'Google Doc';
    return 'Not PDF or Word';
  }

  close(): void {
    this.closed.emit();
  }
}
