import { Component, Input, OnChanges, SimpleChanges, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { NdApiService } from '../../../services/nd/nd-api.service';
import {
  formatChapterLabel,
  formatGovPointDisplayId,
  formatSectionGroupLabel,
} from '../../../../lib/gov-point-filter';
import { NdPointNumberTreeComponent } from '../shared/nd-point-number-tree.component';
import {
  buildLibraryStoredPointDisplay,
  buildPointNumberTree,
  chapterTreeFromRows,
  formatPointCountSummary,
  mapLibrarySnapshotToSourced,
  prepareLibraryPointsForAnalysis,
  type GovPointDuplicateGroup,
  type LibraryPointDisplayRow,
  type LibraryPointDisplayTree,
  type SourcedGovPoint,
} from '../../../../lib/library-points-utils';
import { parsePointSnapshot } from '../../../../lib/nd/utils';
import type { GovPoint } from '../../../../lib/gov-point-filter';
import type { RegulationDocument } from '../../../../lib/nd/types';

type ApiLibraryPoint = {
  regulationPointId: string;
  regulationDocumentId: string;
  displayOrder: number;
  pointSnapshot?: string | Record<string, unknown>;
};

@Component({
  selector: 'app-nd-library-points-panel',
  standalone: true,
  imports: [CommonModule, FormsModule, NdPointNumberTreeComponent],
  templateUrl: './nd-library-points-panel.component.html',
  styleUrl: './nd-library-points-panel.component.scss',
})
export class NdLibraryPointsPanelComponent implements OnChanges {
  private readonly api = inject(NdApiService);

  @Input({ required: true }) libraryId = '';
  @Input() libraryName = '';
  @Input() libraryDescription = '';

  loading = false;
  error = '';
  pointSearch = '';

  libraryStoredCount = 0;
  analyseCount = 0;
  libraryDuplicateCount = 0;
  displayTree: LibraryPointDisplayTree[] = [];
  libraryDuplicateGroups: GovPointDuplicateGroup[] = [];
  catalogPoints: GovPoint[] = [];

  expandedLibraryIds = new Set<string>();
  expandedLibraryDocKeys = new Set<string>();
  expandedChapterKeys = new Set<string>();
  duplicatesExpanded = false;

  readonly formatGovPointDisplayId = formatGovPointDisplayId;
  readonly formatChapterLabel = formatChapterLabel;
  readonly formatSectionGroupLabel = formatSectionGroupLabel;

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['libraryId'] && this.libraryId) {
      void this.loadLibrary();
    }
  }

  get statusLabel(): string {
    if (this.loading) return 'Loading regulation points library…';
    if (this.error) return this.error;
    if (!this.libraryStoredCount) return 'No points in this library.';
    return formatPointCountSummary({
      storedCount: this.libraryStoredCount,
      analyseCount: this.analyseCount,
      skippedCount: 0,
      comparable: [] as GovPoint[],
      skipped: [] as Array<{ point: GovPoint; reason: string }>,
    });
  }

  get visibleDisplay(): LibraryPointDisplayTree[] {
    const q = this.pointSearch.trim().toLowerCase();
    if (!q) return this.displayTree;

    return this.displayTree
      .map((lib) => ({
        ...lib,
        documents: lib.documents
          .map((doc) => this.filterDocForSearch(doc, q))
          .filter((doc) => doc.storedCount > 0),
        analyseCount: 0,
        storedCount: 0,
      }))
      .map((lib) => {
        const storedCount = lib.documents.reduce((n, d) => n + d.storedCount, 0);
        const analyseCount = lib.documents.reduce((n, d) => n + d.analyseCount, 0);
        return { ...lib, storedCount, analyseCount };
      })
      .filter((lib) => lib.documents.length > 0);
  }

  toggleLibrary(libKey: string): void {
    const next = new Set(this.expandedLibraryIds);
    if (next.has(libKey)) next.delete(libKey);
    else next.add(libKey);
    this.expandedLibraryIds = next;
  }

  isLibraryExpanded(libKey: string): boolean {
    return this.expandedLibraryIds.has(libKey);
  }

  toggleDoc(docKey: string): void {
    const next = new Set(this.expandedLibraryDocKeys);
    if (next.has(docKey)) next.delete(docKey);
    else {
      next.add(docKey);
      const doc = this.displayTree
        .flatMap((lib) => lib.documents)
        .find((d) => d.key === docKey);
      if (doc?.useChapters) {
        const chapterKeys = new Set(this.expandedChapterKeys);
        for (const ch of doc.chapters) {
          chapterKeys.add(this.chapterKey(docKey, ch.chapter));
        }
        this.expandedChapterKeys = chapterKeys;
      }
    }
    this.expandedLibraryDocKeys = next;
  }

  isDocExpanded(docKey: string): boolean {
    return this.expandedLibraryDocKeys.has(docKey);
  }

  chapterKey(docKey: string, chapter: string): string {
    return `${docKey}:${chapter}`;
  }

  toggleChapter(docKey: string, chapter: string): void {
    const key = this.chapterKey(docKey, chapter);
    const next = new Set(this.expandedChapterKeys);
    if (next.has(key)) next.delete(key);
    else next.add(key);
    this.expandedChapterKeys = next;
  }

  isChapterExpanded(docKey: string, chapter: string): boolean {
    return this.expandedChapterKeys.has(this.chapterKey(docKey, chapter));
  }

  showSectionBar(
    sections: { key: string }[],
    key: string,
    chapter: string,
  ): boolean {
    return sections.length > 1 || key !== chapter;
  }

  truncate(text: string, max: number): string {
    const t = text.trim();
    if (t.length <= max) return t;
    return `${t.slice(0, max)}…`;
  }

  private filterDocForSearch(
    doc: LibraryPointDisplayTree['documents'][number],
    q: string,
  ): LibraryPointDisplayTree['documents'][number] {
    const rowMatches = (row: LibraryPointDisplayRow) => this.rowMatchesSearch(row, q);

    if (doc.useChapters) {
      const chapters = doc.chapters
        .map((ch) => {
          const sections = ch.sections
            .map((sec) => ({
              ...sec,
              rows: sec.rows.filter(rowMatches),
            }))
            .filter((sec) => sec.rows.length > 0);
          return {
            ...ch,
            sections,
            tree: chapterTreeFromRows(ch.chapter, sections.flatMap((s) => s.rows)),
            storedCount: sections.reduce((n, s) => n + s.rows.length, 0),
            analyseCount: sections.reduce(
              (n, s) => n + s.rows.filter((r) => r.forAnalysis).length,
              0,
            ),
          };
        })
        .filter((ch) => ch.sections.length > 0);
      const storedCount = chapters.reduce((n, ch) => n + ch.storedCount, 0);
      const analyseCount = chapters.reduce((n, ch) => n + ch.analyseCount, 0);
      return { ...doc, chapters, flatRows: [], storedCount, analyseCount };
    }

    const flatRows = doc.flatRows.filter(rowMatches);
    return {
      ...doc,
      flatRows,
      chapters: [],
      pointTree: buildPointNumberTree(flatRows),
      storedCount: flatRows.length,
      analyseCount: flatRows.filter((r) => r.forAnalysis).length,
    };
  }

  private rowMatchesSearch(row: LibraryPointDisplayRow, q: string): boolean {
    const p = row.point;
    return (
      row.displayId.toLowerCase().includes(q) ||
      (p.title ?? '').toLowerCase().includes(q) ||
      p.text.toLowerCase().includes(q)
    );
  }

  private async loadLibrary(): Promise<void> {
    this.loading = true;
    this.error = '';
    this.pointSearch = '';
    this.duplicatesExpanded = false;

    const [res, docNames] = await Promise.all([
      this.api.getLibrary(this.libraryId),
      this.loadRegulationDocNames(),
    ]);

    if (!res.success || !res.data) {
      this.error = res.message ?? 'Failed to load regulation points library';
      this.loading = false;
      return;
    }

    const data = res.data as Record<string, unknown>;
    const nested = data['library'] as Record<string, unknown> | undefined;
    const name =
      this.libraryName ||
      String(data['name'] ?? nested?.['name'] ?? '').trim() ||
      'Regulation points library';

    const rawPoints = (data['points'] ?? nested?.['points'] ?? []) as ApiLibraryPoint[];
    const allRaw: SourcedGovPoint[] = rawPoints.map((p) => {
      const snap = this.parseSnapshot(p.pointSnapshot);
      const docId = String(p.regulationDocumentId);
      return mapLibrarySnapshotToSourced(
        { ...p, pointSnapshot: snap },
        {
          libraryId: this.libraryId,
          libraryName: name,
          docName: docNames.get(docId) ?? 'Regulation document',
        },
      );
    });

    const prepared = prepareLibraryPointsForAnalysis(allRaw);
    this.catalogPoints = allRaw.map((p) => ({
      point_id: p.point_id,
      title: p.title,
      text: p.text,
      section: p.section,
    }));
    this.libraryStoredCount = prepared.storedCount;
    this.libraryDuplicateCount = prepared.duplicateGroups.reduce(
      (n, g) => n + g.duplicates.length,
      0,
    );
    this.libraryDuplicateGroups = prepared.duplicateGroups;
    this.displayTree = buildLibraryStoredPointDisplay(allRaw, prepared.unique);
    this.analyseCount = this.displayTree.reduce((n, lib) => n + lib.analyseCount, 0);

    this.expandedLibraryIds = new Set();
    this.expandedLibraryDocKeys = new Set();
    this.expandedChapterKeys = new Set();
    if (this.displayTree.length) {
      for (const lib of this.displayTree) {
        this.expandedLibraryIds.add(lib.key);
        for (const doc of lib.documents) {
          this.expandedLibraryDocKeys.add(doc.key);
          if (doc.useChapters) {
            for (const ch of doc.chapters) {
              this.expandedChapterKeys.add(this.chapterKey(doc.key, ch.chapter));
            }
          }
        }
      }
    }

    this.loading = false;
  }

  private async loadRegulationDocNames(): Promise<Map<string, string>> {
    const names = new Map<string, string>();
    const res = await this.api.getRegulationDocuments();
    if (res.success && res.data) {
      for (const d of res.data as RegulationDocument[]) {
        names.set(d.id, d.name);
      }
    }
    return names;
  }

  private parseSnapshot(snapshot: ApiLibraryPoint['pointSnapshot']): Record<string, unknown> {
    if (!snapshot) return {};
    if (typeof snapshot === 'string') return parsePointSnapshot(snapshot) as Record<string, unknown>;
    return snapshot;
  }
}
