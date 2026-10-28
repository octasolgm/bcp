import { Component, EventEmitter, Input, OnChanges, OnInit, Output, SimpleChanges, inject, ChangeDetectorRef } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { NdApiService } from '../../../services/nd/nd-api.service';
import type { LibraryPointInput, RegulationDocument, RegulationPoint } from '../../../../lib/nd/types';
import {
  dedupeRegulationDocuments,
  formatRegulationPointLabel,
  manualRegulationPointToGovPoint,
  normalizeRegulationPoint,
  prepareRegulationPointsResponse,
  regulationPointToGovPoint,
  sortRegulationDocuments,
} from '../../../../lib/regulation-catalog-utils';
import {
  formatChapterLabel,
  formatGovPointDisplayId,
  formatSectionGroupLabel,
  groupGovPointsForPicker,
  type GovPoint,
  type GovPointChapterGroup,
} from '../../../../lib/gov-point-filter';
import {
  fingerprintFromSnapshot,
  libraryPointFingerprint,
} from '../../../../lib/library-points-utils';

function buildDisplayDocPoints(raw: RegulationPoint[], isManual = false): {
  points: RegulationPoint[];
  chapterGroups: GovPointChapterGroup[];
} {
  const toGov = (p: RegulationPoint) =>
    isManual ? manualRegulationPointToGovPoint(p) : regulationPointToGovPoint(p);
  return {
    points: raw,
    chapterGroups: groupGovPointsForPicker(raw.map(toGov)),
  };
}

type SelectedPoint = LibraryPointInput & { label: string; fingerprint: string };

type AddPointsSummary = {
  newlyAdded: number;
  alreadyAdded: number;
  duplicates: Array<{ label: string; existingLabel: string }>;
};

type DocPointsGroup = {
  docId: string;
  docName: string;
  points: RegulationPoint[];
  chapterGroups: GovPointChapterGroup[];
  loading: boolean;
  expanded: boolean;
  expandedChapters: Set<string>;
};

@Component({
  selector: 'app-nd-library-points-picker',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './nd-library-points-picker.component.html',
  styleUrls: ['./nd-library-points-picker.component.scss'],
})
export class NdLibraryPointsPickerComponent implements OnInit, OnChanges {
  private readonly api = inject(NdApiService);
  private readonly cdr = inject(ChangeDetectorRef);

  @Input() initialPoints: LibraryPointInput[] = [];
  @Input() revision = 0;
  @Output() pointsChange = new EventEmitter<LibraryPointInput[]>();

  docs: RegulationDocument[] = [];
  selectedDocIds = new Set<string>();
  docGroups: DocPointsGroup[] = [];
  selected: SelectedPoint[] = [];
  checkedPointIds = new Set<string>();
  selectedLibraryPointIds = new Set<string>();

  loading = true;
  error = '';
  addSummary: AddPointsSummary | null = null;
  selectedDuplicatesExpanded = false;
  private initialPointsApplied = false;

  readonly formatGovPointDisplayId = formatGovPointDisplayId;
  readonly formatRegulationPointLabel = formatRegulationPointLabel;
  readonly formatSectionGroupLabel = formatSectionGroupLabel;
  readonly formatChapterLabel = formatChapterLabel;

  async ngOnInit(): Promise<void> {
    await this.loadDocs();
  }

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['revision'] && !changes['revision'].firstChange) {
      this.initialPointsApplied = false;
      this.selectedDocIds = new Set();
      this.docGroups = [];
      this.checkedPointIds = new Set();
      this.selectedLibraryPointIds = new Set();
      void this.applyInitialPoints();
      return;
    }
    if (changes['initialPoints'] && this.initialPoints.length > 0) {
      void this.applyInitialPoints();
    }
  }

  getSelectedPoints(): LibraryPointInput[] {
    return this.selected.map((p, i) => ({
      regulationPointId: p.regulationPointId,
      regulationDocumentId: p.regulationDocumentId,
      displayOrder: i + 1,
      pointSnapshot: p.pointSnapshot,
    }));
  }

  get selectedDocIdsList(): string[] {
    return this.docs.filter((d) => this.selectedDocIds.has(d.id)).map((d) => d.id);
  }

  get selectedDuplicateGroups(): Array<{ fingerprint: string; label: string; items: SelectedPoint[] }> {
    const byFp = new Map<string, SelectedPoint[]>();
    for (const p of this.selected) {
      const list = byFp.get(p.fingerprint) ?? [];
      list.push(p);
      byFp.set(p.fingerprint, list);
    }
    return [...byFp.entries()]
      .filter(([, items]) => items.length > 1)
      .map(([fingerprint, items]) => ({
        fingerprint,
        label: items[0].label,
        items,
      }));
  }

  get selectedUniqueCount(): number {
    return new Set(this.selected.map((p) => p.fingerprint)).size;
  }

  toggleSelectedDuplicates(): void {
    this.selectedDuplicatesExpanded = !this.selectedDuplicatesExpanded;
  }

  dismissAddSummary(): void {
    this.addSummary = null;
  }

  docPointMeta(doc: RegulationDocument): string {
    return `${doc.pointCount ?? 0} pts · ${doc.extractionStatus}`;
  }

  docPointCountLabel(docId: string): string {
    const loaded = this.groupForDoc(docId)?.points.length;
    const doc = this.docs.find((d) => d.id === docId);
    return `${loaded ?? doc?.pointCount ?? 0} pts`;
  }

  async loadDocs(): Promise<void> {
    this.loading = true;
    this.error = '';
    const res = await this.api.getRegulationDocuments();
    if (res.success && res.data) {
      const all = res.data as RegulationDocument[];
      this.docs = sortRegulationDocuments(dedupeRegulationDocuments(all));
    } else {
      this.error = res.message ?? 'Failed to load regulation documents';
    }
    this.loading = false;
    await this.applyInitialPoints();
  }

  isDocSelected(docId: string): boolean {
    return this.selectedDocIds.has(docId);
  }

  async onDocRowClick(doc: RegulationDocument): Promise<void> {
    await this.toggleDoc(doc);
  }

  async selectAllDocs(): Promise<void> {
    for (const doc of this.docs) {
      if (!this.selectedDocIds.has(doc.id)) await this.toggleDoc(doc);
    }
    this.cdr.markForCheck();
  }

  clearAllDocs(): void {
    this.selectedDocIds = new Set();
    this.docGroups = [];
    this.checkedPointIds = new Set();
    this.cdr.markForCheck();
  }

  async toggleDoc(doc: RegulationDocument): Promise<void> {
    if (this.selectedDocIds.has(doc.id)) {
      const group = this.groupForDoc(doc.id);
      if (group) {
        const nextChecked = new Set(this.checkedPointIds);
        for (const pt of group.points) nextChecked.delete(pt.id);
        this.checkedPointIds = nextChecked;
      }
      const nextSelected = new Set(this.selectedDocIds);
      nextSelected.delete(doc.id);
      this.selectedDocIds = nextSelected;
      this.docGroups = this.docGroups.filter((g) => g.docId !== doc.id);
      this.cdr.markForCheck();
      return;
    }

    this.selectedDocIds = new Set([...this.selectedDocIds, doc.id]);
    this.docGroups = [
      ...this.docGroups,
      {
        docId: doc.id,
        docName: doc.name,
        points: [],
        chapterGroups: [],
        loading: true,
        expanded: true,
        expandedChapters: new Set<string>(),
      },
    ];
    await this.loadPointsForDoc(doc.id);
    this.cdr.markForCheck();
  }

  toggleDocExpanded(docId: string): void {
    this.docGroups = this.docGroups.map((g) =>
      g.docId === docId ? { ...g, expanded: !g.expanded } : g,
    );
  }

  isChapterExpanded(docId: string, chapter: string): boolean {
    const group = this.groupForDoc(docId);
    return group?.expandedChapters.has(chapter) ?? false;
  }

  toggleChapter(docId: string, chapter: string): void {
    this.docGroups = this.docGroups.map((g) => {
      if (g.docId !== docId) return g;
      const next = new Set(g.expandedChapters);
      if (next.has(chapter)) next.delete(chapter);
      else next.add(chapter);
      return { ...g, expandedChapters: next };
    });
  }

  showSectionBar(sections: GovPointChapterGroup['sections'], key: string, chapter: string): boolean {
    return sections.length > 1 || key !== chapter;
  }

  pointForGov(group: DocPointsGroup, gov: GovPoint): RegulationPoint | undefined {
    const govId = gov.point_id.trim().replace(/\.$/, '');
    const exact = group.points.find(
      (p) => p.pointNumber.trim().replace(/\.$/, '') === govId,
    );
    if (exact) return exact;

    return group.points.find((p) => {
      const num = p.pointNumber.trim().replace(/\.$/, '');
      return govId === num || govId.startsWith(`${num}.`);
    });
  }

  checkedCountForDoc(docId: string): number {
    const group = this.groupForDoc(docId);
    if (!group) return 0;
    return group.points.filter((pt) => this.checkedPointIds.has(pt.id)).length;
  }

  checkedCountForChapter(docId: string, chapter: string): number {
    const group = this.groupForDoc(docId);
    if (!group) return 0;
    const ch = group.chapterGroups.find((c) => c.chapter === chapter);
    if (!ch) return 0;
    let count = 0;
    for (const sec of ch.sections) {
      for (const gov of sec.points) {
        const pt = this.pointForGov(group, gov);
        if (pt && this.checkedPointIds.has(pt.id)) count++;
      }
    }
    return count;
  }

  selectablePointsInChapter(docId: string, chapter: string): RegulationPoint[] {
    const group = this.groupForDoc(docId);
    if (!group) return [];
    const ch = group.chapterGroups.find((c) => c.chapter === chapter);
    if (!ch) return [];
    const pts: RegulationPoint[] = [];
    for (const sec of ch.sections) {
      for (const gov of sec.points) {
        const pt = this.pointForGov(group, gov);
        if (pt && !this.isInLibrary(pt.id)) pts.push(pt);
      }
    }
    return pts;
  }

  selectablePointsInSection(docId: string, sectionKey: string): RegulationPoint[] {
    const group = this.groupForDoc(docId);
    if (!group) return [];
    const pts: RegulationPoint[] = [];
    for (const ch of group.chapterGroups) {
      const sec = ch.sections.find((s) => s.key === sectionKey);
      if (!sec) continue;
      for (const gov of sec.points) {
        const pt = this.pointForGov(group, gov);
        if (pt && !this.isInLibrary(pt.id)) pts.push(pt);
      }
    }
    return pts;
  }

  selectablePointsInDoc(docId: string): RegulationPoint[] {
    const group = this.groupForDoc(docId);
    if (!group) return [];
    return group.points.filter((pt) => !this.isInLibrary(pt.id));
  }

  isDocPointsAllChecked(docId: string): boolean {
    const selectable = this.selectablePointsInDoc(docId);
    return selectable.length > 0 && selectable.every((pt) => this.checkedPointIds.has(pt.id));
  }

  isDocPointsPartiallyChecked(docId: string): boolean {
    const selectable = this.selectablePointsInDoc(docId);
    const checked = selectable.filter((pt) => this.checkedPointIds.has(pt.id)).length;
    return checked > 0 && checked < selectable.length;
  }

  isChapterAllChecked(docId: string, chapter: string): boolean {
    const selectable = this.selectablePointsInChapter(docId, chapter);
    return selectable.length > 0 && selectable.every((pt) => this.checkedPointIds.has(pt.id));
  }

  isChapterPartiallyChecked(docId: string, chapter: string): boolean {
    const selectable = this.selectablePointsInChapter(docId, chapter);
    const checked = selectable.filter((pt) => this.checkedPointIds.has(pt.id)).length;
    return checked > 0 && checked < selectable.length;
  }

  isSectionAllChecked(docId: string, sectionKey: string): boolean {
    const selectable = this.selectablePointsInSection(docId, sectionKey);
    return selectable.length > 0 && selectable.every((pt) => this.checkedPointIds.has(pt.id));
  }

  isSectionPartiallyChecked(docId: string, sectionKey: string): boolean {
    const selectable = this.selectablePointsInSection(docId, sectionKey);
    const checked = selectable.filter((pt) => this.checkedPointIds.has(pt.id)).length;
    return checked > 0 && checked < selectable.length;
  }

  toggleDocPointsSelection(docId: string, checked: boolean): void {
    const selectable = this.selectablePointsInDoc(docId);
    const next = new Set(this.checkedPointIds);
    for (const pt of selectable) {
      if (checked) next.add(pt.id);
      else next.delete(pt.id);
    }
    this.checkedPointIds = next;
  }

  toggleChapterSelection(docId: string, chapter: string, checked: boolean): void {
    const selectable = this.selectablePointsInChapter(docId, chapter);
    const next = new Set(this.checkedPointIds);
    for (const pt of selectable) {
      if (checked) next.add(pt.id);
      else next.delete(pt.id);
    }
    this.checkedPointIds = next;
  }

  toggleSectionSelection(docId: string, sectionKey: string, checked: boolean): void {
    const selectable = this.selectablePointsInSection(docId, sectionKey);
    const next = new Set(this.checkedPointIds);
    for (const pt of selectable) {
      if (checked) next.add(pt.id);
      else next.delete(pt.id);
    }
    this.checkedPointIds = next;
  }

  selectAllInDoc(docId: string): void {
    const group = this.groupForDoc(docId);
    if (!group) return;
    const next = new Set(this.checkedPointIds);
    for (const pt of group.points) {
      if (!this.isInLibrary(pt.id)) next.add(pt.id);
    }
    this.checkedPointIds = next;
  }

  selectAllInChapter(docId: string, chapter: string): void {
    const group = this.groupForDoc(docId);
    if (!group) return;
    const ch = group.chapterGroups.find((c) => c.chapter === chapter);
    if (!ch) return;
    const next = new Set(this.checkedPointIds);
    for (const sec of ch.sections) {
      for (const gov of sec.points) {
        const pt = this.pointForGov(group, gov);
        if (pt && !this.isInLibrary(pt.id)) next.add(pt.id);
      }
    }
    this.checkedPointIds = next;
  }

  selectAllInSection(docId: string, sectionKey: string): void {
    const group = this.groupForDoc(docId);
    if (!group) return;
    const next = new Set(this.checkedPointIds);
    for (const ch of group.chapterGroups) {
      const sec = ch.sections.find((s) => s.key === sectionKey);
      if (!sec) continue;
      for (const gov of sec.points) {
        const pt = this.pointForGov(group, gov);
        if (pt && !this.isInLibrary(pt.id)) next.add(pt.id);
      }
    }
    this.checkedPointIds = next;
  }

  clearCheckedForDoc(docId: string): void {
    const group = this.groupForDoc(docId);
    if (!group) return;
    const next = new Set(this.checkedPointIds);
    for (const pt of group.points) next.delete(pt.id);
    this.checkedPointIds = next;
  }

  collapseAllDocs(): void {
    this.docGroups = this.docGroups.map((g) => ({
      ...g,
      expanded: false,
      expandedChapters: new Set<string>(),
    }));
  }

  expandAllDocs(): void {
    this.docGroups = this.docGroups.map((g) => ({
      ...g,
      expanded: true,
      expandedChapters: new Set(g.chapterGroups.map((ch) => ch.chapter)),
    }));
  }

  private async loadPointsForDoc(docId: string): Promise<void> {
    this.docGroups = this.docGroups.map((g) =>
      g.docId === docId ? { ...g, loading: true } : g,
    );

    const res = await this.api.getDocumentPoints(docId);
    const doc = this.docs.find((d) => d.id === docId);
    const isManual = doc?.isManual === true || doc?.source === 'manual';
    const prepared = prepareRegulationPointsResponse(
      res.success && res.data ? (res.data as unknown[]) : [],
      { docName: doc?.name, apiPointCount: res.pointCount },
    );
    const { points, chapterGroups } = buildDisplayDocPoints(prepared.points, isManual);
    const expandedChapters = new Set(chapterGroups.map((ch) => ch.chapter));

    const docIdx = this.docs.findIndex((d) => d.id === docId);
    if (docIdx >= 0) {
      const listCount = this.docs[docIdx].pointCount ?? 0;
      const storedCount =
        listCount > 0 && prepared.storedCount > listCount ? listCount : prepared.storedCount;
      this.docs[docIdx] = { ...this.docs[docIdx], pointCount: storedCount };
    }

    if (!res.success) {
      this.error = res.message ?? 'Failed to load points';
    } else if (points.length === 0) {
      const doc = this.docs.find((d) => d.id === docId);
      if ((doc?.pointCount ?? 0) > 0) {
        this.error = `No extracted points for ${doc?.name ?? 'document'}.`;
      }
    }

    this.docGroups = this.docGroups.map((g) =>
      g.docId === docId
        ? { ...g, points, chapterGroups, expandedChapters, loading: false }
        : g,
    );
    this.cdr.markForCheck();
  }

  private findDocForId(docId: string): RegulationDocument | undefined {
    return this.docs.find((d) => d.id === docId || d.storedDocumentId === docId);
  }

  private async applyInitialPoints(): Promise<void> {
    if (this.loading || this.docs.length === 0) return;
    if (this.initialPoints.length === 0) {
      this.initialPointsApplied = false;
      return;
    }
    if (this.initialPointsApplied) return;

    this.initialPointsApplied = true;
    this.selected = this.initialPoints.map((p) => this.toSelectedPoint(p));

    const docIds = [...new Set(this.initialPoints.map((p) => p.regulationDocumentId))];
    for (const savedDocId of docIds) {
      const doc = this.findDocForId(savedDocId);
      const loadId = doc?.id ?? savedDocId;
      if (this.selectedDocIds.has(loadId)) continue;
      this.selectedDocIds = new Set([...this.selectedDocIds, loadId]);
      this.docGroups = [
        ...this.docGroups,
        {
          docId: loadId,
          docName: doc?.name ?? 'Regulation document',
          points: [],
          chapterGroups: [],
          loading: true,
          expanded: true,
          expandedChapters: new Set<string>(),
        },
      ];
      await this.loadPointsForDoc(loadId);
    }
    this.emitPoints();
    this.cdr.markForCheck();
  }

  groupForDoc(docId: string): DocPointsGroup | undefined {
    return this.docGroups.find((g) => g.docId === docId);
  }

  get totalLoadedPoints(): number {
    return this.selectedDocIdsList.reduce(
      (sum, docId) => sum + (this.groupForDoc(docId)?.points.length ?? 0),
      0,
    );
  }

  get totalCheckedCount(): number {
    let count = 0;
    for (const docId of this.selectedDocIdsList) {
      count += this.checkedCountForDoc(docId);
    }
    return count;
  }

  get hasImportableInSelection(): boolean {
    for (const docId of this.selectedDocIdsList) {
      if (this.selectablePointsInDoc(docId).length > 0) return true;
    }
    return false;
  }

  get isAnySelectedDocLoading(): boolean {
    return this.docGroups.some((g) => this.selectedDocIds.has(g.docId) && g.loading);
  }

  private pointFingerprint(pt: RegulationPoint): string {
    return libraryPointFingerprint(pt.pointNumber, pt.pointTitle, pt.pointContent);
  }

  private selectedFingerprint(p: LibraryPointInput | SelectedPoint): string {
    if ('fingerprint' in p && p.fingerprint) return p.fingerprint;
    const snap = this.parseSnapshot(p.pointSnapshot);
    return fingerprintFromSnapshot(snap);
  }

  private findSelectedByFingerprint(fp: string): SelectedPoint | undefined {
    return this.selected.find((p) => p.fingerprint === fp);
  }

  isInLibrary(pointId: string, pt?: RegulationPoint): boolean {
    if (this.selected.some((p) => p.regulationPointId === pointId)) return true;
    if (pt) {
      const fp = this.pointFingerprint(pt);
      return this.selected.some((p) => p.fingerprint === fp);
    }
    return false;
  }

  isDuplicateContent(pt: RegulationPoint): boolean {
    const fp = this.pointFingerprint(pt);
    return Boolean(this.findSelectedByFingerprint(fp));
  }

  duplicateOfLabel(pt: RegulationPoint): string | null {
    const match = this.findSelectedByFingerprint(this.pointFingerprint(pt));
    return match?.label ?? null;
  }

  private recordAddSummary(checked: RegulationPoint[], added: RegulationPoint[]): void {
    const already = checked.filter((pt) => this.isInLibrary(pt.id, pt) && !added.includes(pt));
    const duplicates = already.map((pt) => ({
      label: formatRegulationPointLabel(pt),
      existingLabel: this.duplicateOfLabel(pt) ?? 'Already in library',
    }));
    if (added.length || duplicates.length) {
      this.addSummary = {
        newlyAdded: added.length,
        alreadyAdded: duplicates.length,
        duplicates,
      };
    }
  }

  isChecked(pointId: string): boolean {
    return this.checkedPointIds.has(pointId);
  }

  toggleChecked(pointId: string, checked: boolean): void {
    const next = new Set(this.checkedPointIds);
    if (checked) next.add(pointId);
    else next.delete(pointId);
    this.checkedPointIds = next;
  }

  addPoint(pt: RegulationPoint, docId: string): void {
    if (this.isInLibrary(pt.id, pt)) {
      this.recordAddSummary([pt], []);
      return;
    }
    this.selected = [...this.selected, this.buildSelectedPoint(pt, docId)];
    this.recordAddSummary([pt], [pt]);
    const next = new Set(this.checkedPointIds);
    next.delete(pt.id);
    this.checkedPointIds = next;
    this.emitPoints();
  }

  addCheckedPoints(docId: string): void {
    this.addAllCheckedPoints([docId]);
  }

  addAllCheckedPoints(docIds: string[] = this.selectedDocIdsList): void {
    const checkedPairs: Array<{ pt: RegulationPoint; docId: string }> = [];
    for (const docId of docIds) {
      const group = this.groupForDoc(docId);
      if (!group) continue;
      for (const pt of group.points) {
        if (this.checkedPointIds.has(pt.id)) checkedPairs.push({ pt, docId });
      }
    }
    const checked = checkedPairs.map((x) => x.pt);
    let nextSelected = [...this.selected];
    const isInLibrarySnap = (pt: RegulationPoint) => {
      const fp = this.pointFingerprint(pt);
      return nextSelected.some((p) => p.regulationPointId === pt.id || p.fingerprint === fp);
    };
    const toAddPairs = checkedPairs.filter(({ pt }) => !isInLibrarySnap(pt));
    const toAdd = toAddPairs.map((x) => x.pt);
    this.recordAddSummary(checked, toAdd);
    if (toAddPairs.length === 0) {
      this.cdr.markForCheck();
      return;
    }
    for (const { pt, docId } of toAddPairs) {
      nextSelected.push(this.buildSelectedPoint(pt, docId));
    }
    this.selected = nextSelected;
    const nextChecked = new Set(this.checkedPointIds);
    for (const pt of toAdd) nextChecked.delete(pt.id);
    this.checkedPointIds = nextChecked;
    this.emitPoints();
  }

  clearAllChecked(): void {
    this.checkedPointIds = new Set();
  }

  importAllFromDoc(docId: string): void {
    this.importAllFromDocs([docId]);
  }

  importAllFromAllDocs(): void {
    this.importAllFromDocs(this.selectedDocIdsList);
  }

  private importAllFromDocs(docIds: string[]): void {
    const allConsidered: RegulationPoint[] = [];
    const allAdded: RegulationPoint[] = [];
    let nextSelected = [...this.selected];
    const isInLibrarySnap = (pt: RegulationPoint) => {
      const fp = this.pointFingerprint(pt);
      return nextSelected.some((p) => p.regulationPointId === pt.id || p.fingerprint === fp);
    };

    for (const docId of docIds) {
      const group = this.groupForDoc(docId);
      if (!group || group.loading) continue;
      allConsidered.push(...group.points);
      const toAdd = group.points.filter((pt) => !isInLibrarySnap(pt));
      for (const pt of toAdd) {
        nextSelected.push(this.buildSelectedPoint(pt, docId));
        allAdded.push(pt);
      }
    }

    this.recordAddSummary(allConsidered, allAdded);
    if (allAdded.length === 0) {
      this.cdr.markForCheck();
      return;
    }
    this.selected = nextSelected;
    this.checkedPointIds = new Set();
    this.emitPoints();
  }

  get selectedLibraryCount(): number {
    return this.selectedLibraryPointIds.size;
  }

  isLibraryPointChecked(pointId: string): boolean {
    return this.selectedLibraryPointIds.has(pointId);
  }

  toggleLibraryPointChecked(pointId: string, checked: boolean): void {
    const next = new Set(this.selectedLibraryPointIds);
    if (checked) next.add(pointId);
    else next.delete(pointId);
    this.selectedLibraryPointIds = next;
  }

  isAllLibraryPointsChecked(): boolean {
    return this.selected.length > 0 && this.selected.every((p) => this.selectedLibraryPointIds.has(p.regulationPointId));
  }

  isPartialLibraryPointsChecked(): boolean {
    const n = this.selectedLibraryCount;
    return n > 0 && n < this.selected.length;
  }

  toggleAllLibraryPoints(checked: boolean): void {
    if (!checked) {
      this.selectedLibraryPointIds = new Set();
      return;
    }
    this.selectedLibraryPointIds = new Set(this.selected.map((p) => p.regulationPointId));
  }

  clearLibraryPointSelection(): void {
    this.selectedLibraryPointIds = new Set();
  }

  removeSelectedLibraryPoints(): void {
    if (!this.selectedLibraryCount) return;
    const toRemove = this.selectedLibraryPointIds;
    this.selected = this.selected.filter((p) => !toRemove.has(p.regulationPointId));
    this.selectedLibraryPointIds = new Set();
    this.emitPoints();
  }

  removePoint(pointId: string): void {
    this.selected = this.selected.filter((p) => p.regulationPointId !== pointId);
    const next = new Set(this.selectedLibraryPointIds);
    next.delete(pointId);
    this.selectedLibraryPointIds = next;
    this.emitPoints();
  }

  clearAll(): void {
    this.selected = [];
    this.checkedPointIds = new Set();
    this.selectedLibraryPointIds = new Set();
    this.emitPoints();
  }

  movePoint(index: number, dir: -1 | 1): void {
    const target = index + dir;
    if (target < 0 || target >= this.selected.length) return;
    const next = [...this.selected];
    [next[index], next[target]] = [next[target], next[index]];
    this.selected = next;
    this.emitPoints();
  }

  truncate(text: string, max: number): string {
    const t = text.trim();
    if (t.length <= max) return t;
    return `${t.slice(0, max)}…`;
  }

  private buildSelectedPoint(pt: RegulationPoint, docId: string): SelectedPoint {
    const snap = {
      pointNumber: pt.pointNumber,
      pointTitle: pt.pointTitle,
      pointContent: pt.pointContent,
      pageReference: pt.pageReference,
      regulationDocumentId: docId,
      regulationPointId: pt.id,
      isIntroductionPoint: pt.isIntroductionPoint ?? false,
      isAnnexPoint: pt.isAnnexPoint ?? false,
    };
    const fingerprint = this.pointFingerprint(pt);
    return {
      regulationPointId: pt.id,
      regulationDocumentId: docId,
      displayOrder: this.selected.length + 1,
      pointSnapshot: snap,
      label: formatRegulationPointLabel(pt),
      fingerprint,
    };
  }

  private toSelectedPoint(p: LibraryPointInput): SelectedPoint {
    const snap = this.parseSnapshot(p.pointSnapshot);
    const title = String(snap['pointTitle'] ?? '');
    const num = String(snap['pointNumber'] ?? '');
    const fingerprint = fingerprintFromSnapshot(snap);
    return {
      ...p,
      label: formatRegulationPointLabel({ pointNumber: num, pointTitle: title }),
      fingerprint,
    };
  }

  private parseSnapshot(snapshot: LibraryPointInput['pointSnapshot']): Record<string, unknown> {
    if (!snapshot) return {};
    if (typeof snapshot === 'string') {
      try {
        return JSON.parse(snapshot) as Record<string, unknown>;
      } catch {
        return {};
      }
    }
    return snapshot;
  }

  private emitPoints(): void {
    this.pointsChange.emit(
      this.selected.map((p, i) => ({
        regulationPointId: p.regulationPointId,
        regulationDocumentId: p.regulationDocumentId,
        displayOrder: i + 1,
        pointSnapshot: p.pointSnapshot,
      })),
    );
  }
}
