import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, NavigationEnd, Router, RouterLink } from '@angular/router';
import { filter } from 'rxjs/operators';
import { NdApiService } from '../../../services/nd/nd-api.service';
import { NdAuthService } from '../../../services/nd/nd-auth.service';
import { formatDate } from '../../../../lib/nd/utils';
import {
  compareDateIso,
  compareNumber,
  compareText,
  hasListFilters,
  matchesSearch,
  nextSortState,
  sortIndicator,
  type SortDir,
} from '../../../../lib/nd/list-utils';
import { formatStoredAnalyseMeta } from '../../../../lib/library-points-utils';
import type { LibrarySummary } from '../../../../lib/nd/types';
import { NdLibraryPointsPanelComponent } from './nd-library-points-panel.component';

type LibrarySortColumn = 'name' | 'description' | 'points' | 'docs' | 'created';

@Component({
  selector: 'app-nd-libraries',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink, NdLibraryPointsPanelComponent],
  templateUrl: './nd-libraries.component.html',
  styleUrls: ['./nd-libraries.component.scss', '../nd-shared.scss'],
})
export class NdLibrariesComponent implements OnInit {
  private readonly api = inject(NdApiService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  readonly auth = inject(NdAuthService);

  libraries: LibrarySummary[] = [];
  loading = true;
  deletingId: string | null = null;
  searchQuery = '';
  sortColumn: LibrarySortColumn = 'created';
  sortDir: SortDir = 'desc';

  selectedLibrary: LibrarySummary | null = null;
  showPointsPanel = false;

  async ngOnInit(): Promise<void> {
    const profile = (await this.auth.refreshProfile()) ?? this.auth.profile();
    if (!profile) {
      this.loading = false;
      return;
    }
    await this.loadLibraries();
    this.openLibraryFromQuery(this.route.snapshot.queryParamMap.get('view'));

    this.route.queryParamMap.subscribe((params) => {
      const viewId = params.get('view');
      if (!viewId) {
        this.showPointsPanel = false;
        this.selectedLibrary = null;
        return;
      }
      this.openLibraryFromQuery(viewId);
    });

    this.router.events
      .pipe(
        filter((e): e is NavigationEnd => e instanceof NavigationEnd),
        filter((e) => /^\/nd\/libraries\/?$/.test(e.urlAfterRedirects.split('?')[0])),
      )
      .subscribe(() => void this.loadLibraries());
  }

  async loadLibraries(): Promise<void> {
    if (!this.libraries.length) this.loading = true;
    const res = await this.api.getLibraries();
    if (res.success && res.data) this.libraries = res.data as LibrarySummary[];
    this.loading = false;
    if (this.selectedLibrary) {
      this.selectedLibrary =
        this.libraries.find((l) => l.id === this.selectedLibrary?.id) ?? this.selectedLibrary;
    }
  }

  get visibleLibraries(): LibrarySummary[] {
    let list = this.libraries.filter((lib) =>
      matchesSearch(this.searchQuery, [lib.name, lib.description]),
    );

    return [...list].sort((a, b) => {
      switch (this.sortColumn) {
        case 'name':
          return compareText(a.name, b.name, this.sortDir);
        case 'description':
          return compareText(a.description ?? '', b.description ?? '', this.sortDir);
        case 'points':
          return compareNumber(a.pointCount ?? 0, b.pointCount ?? 0, this.sortDir);
        case 'docs':
          return compareNumber(a.documentCount ?? 0, b.documentCount ?? 0, this.sortDir);
        case 'created':
        default:
          return compareDateIso(a.createdAt, b.createdAt, this.sortDir);
      }
    });
  }

  get hasActiveFilters(): boolean {
    return hasListFilters(this.searchQuery);
  }

  toggleSort(column: LibrarySortColumn): void {
    const next = nextSortState(this.sortColumn, column, this.sortDir, 'created');
    this.sortColumn = next.column;
    this.sortDir = next.dir;
  }

  sortMark(column: LibrarySortColumn): string {
    return sortIndicator(this.sortColumn, column, this.sortDir);
  }

  clearFilters(): void {
    this.searchQuery = '';
  }

  get canEdit(): boolean {
    const role = this.auth.getRole();
    return role === 'maker' || role === 'super_admin';
  }

  async handleDelete(id: string): Promise<void> {
    if (!confirm('Delete this regulation points library?')) return;
    this.deletingId = id;
    await this.api.deleteLibrary(id);
    this.libraries = this.libraries.filter((l) => l.id !== id);
    if (this.selectedLibrary?.id === id) {
      this.closePointsPanel();
    }
    this.deletingId = null;
  }

  editLibrary(lib: LibrarySummary, event?: Event): void {
    event?.stopPropagation();
    void this.router.navigate(['/nd/libraries', lib.id], {
      state: { library: { name: lib.name, description: lib.description ?? '' } },
    });
  }

  viewLibrary(lib: LibrarySummary, event?: Event): void {
    event?.stopPropagation();
    this.selectedLibrary = lib;
    this.showPointsPanel = true;
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { view: lib.id },
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
  }

  closePointsPanel(): void {
    this.showPointsPanel = false;
    this.selectedLibrary = null;
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { view: null },
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
  }

  isSelected(lib: LibrarySummary): boolean {
    return this.showPointsPanel && this.selectedLibrary?.id === lib.id;
  }

  private openLibraryFromQuery(viewId: string | null): void {
    if (!viewId) return;
    const lib = this.libraries.find((l) => l.id === viewId);
    if (!lib) return;
    this.selectedLibrary = lib;
    this.showPointsPanel = true;
  }

  formatDate = formatDate;
  formatStoredAnalyseMeta = formatStoredAnalyseMeta;
}
