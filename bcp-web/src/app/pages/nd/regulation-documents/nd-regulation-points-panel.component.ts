import {
  ChangeDetectionStrategy,
  ChangeDetectorRef,
  Component,
  EventEmitter,
  Input,
  OnChanges,
  Output,
  SimpleChanges,
  inject,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import {
  buildGovPointDisplayCatalog,
  formatChapterLabel,
  formatGovPointDisplayId,
  formatSectionGroupLabel,
  groupGovPointsForPicker,
  sectionHeadingTitleForKey,
  type GovPoint,
  type GovPointChapterGroup,
} from '../../../../lib/gov-point-filter';
import { formatPointCountSummary } from '../../../../lib/library-points-utils';
import {
  computeRegulationPointStats,
  filterRegulationPointsForDisplay,
  regulationPointToGovPoint,
} from '../../../../lib/regulation-catalog-utils';
import type { RegulationPoint } from '../../../../lib/nd/types';
import { sortByPointRef } from '../../../../lib/nd/list-utils';
import { formatPointPageRef, resolveRegulationPdfPage } from '../../../../lib/nd/regulation-pdf-page';

/** Above this many points, auto-expanding every full text makes the panel unresponsive. */
const MaxAutoExpandPoints = 250;

@Component({
  selector: 'app-nd-regulation-points-panel',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './nd-regulation-points-panel.component.html',
  styleUrl: './nd-regulation-points-panel.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class NdRegulationPointsPanelComponent implements OnChanges {
  private readonly cdr = inject(ChangeDetectorRef);

  @Input() docName = '';
  @Input() points: RegulationPoint[] = [];
  @Input() source = '';
  @Input() loading = false;
  /** Authoritative count from regulation document API (list/detail). Avoids inflating UI when duplicate rows exist. */
  @Input() reportedPointCount: number | null = null;
  @Input() highlightPointNumber = '';
  @Input() canOpenSource = false;
  @Output() openSourcePage = new EventEmitter<number>();

  search = '';
  expandedChapters = new Set<string>();
  expandedSections = new Set<string>();
  expandedPoints = new Set<string>();
  chapterGroups: GovPointChapterGroup[] = [];
  private allGovPoints: GovPoint[] = [];
  private pointsByNumber = new Map<string, RegulationPoint>();
  private rebuildTimer: ReturnType<typeof setTimeout> | null = null;
  readonly previewLen = 120;
  storedCount = 0;
  analyseCount = 0;
  skippedCount = 0;
  skippedReasons = new Map<string, string>();
  introductionCount = 0;
  building = false;

  readonly formatGovPointDisplayId = formatGovPointDisplayId;
  readonly formatSectionGroupLabel = formatSectionGroupLabel;
  readonly formatChapterLabel = formatChapterLabel;

  ngOnChanges(changes: SimpleChanges): void {
    const highlight = this.highlightPointNumber.trim();
    if (highlight && changes['highlightPointNumber']) {
      this.search = highlight;
      this.expandedChapters.clear();
    }

    if (changes['loading'] && this.loading) {
      this.building = false;
      this.cdr.markForCheck();
    }

    const pointsChanged = !!changes['points'] || !!changes['reportedPointCount'] || !!changes['docName'];
    if (!pointsChanged && !changes['highlightPointNumber'] && !changes['loading']) {
      return;
    }

    if (this.rebuildTimer) {
      clearTimeout(this.rebuildTimer);
      this.rebuildTimer = null;
    }

    // Yield so the network response can clear the spinner before heavy catalog work.
    if (this.points.length > 80 && pointsChanged) {
      this.building = true;
      this.cdr.markForCheck();
      this.rebuildTimer = setTimeout(() => {
        this.rebuildTimer = null;
        this.rebuildGroups(highlight);
        this.building = false;
        this.cdr.markForCheck();
      }, 0);
      return;
    }

    this.rebuildGroups(highlight);
    this.building = false;
    this.cdr.markForCheck();
  }

  get statusLabel(): string {
    if (this.loading || this.building) return 'Loading points…';
    if (!this.storedCount) return 'No points extracted yet.';
    const base = formatPointCountSummary({
      storedCount: this.storedCount,
      analyseCount: this.analyseCount,
      skippedCount: this.skippedCount,
      comparable: [],
      skipped: [],
    });
    if (this.introductionCount > 0) {
      return `${base} · ${this.introductionCount} introduction (shown, not used in gap analysis)`;
    }
    return base;
  }

  get viewNote(): string {
    if (!this.storedCount || this.skippedCount === 0) return '';
    return 'Introduction, annex, and section-header points are shown for reference. Gap analysis uses numbered requirement leaves only (from §2 onward).';
  }

  get footnote(): string {
    if (!this.storedCount) return '';
    return `${this.analyseCount}/${this.storedCount} shown for analysis`;
  }

  get visibleChapterGroups(): GovPointChapterGroup[] {
    const q = this.search.trim().toLowerCase();
    if (!q) return this.chapterGroups;
    return this.chapterGroups
      .map((ch) => ({
        ...ch,
        sections: ch.sections
          .map((sec) => ({
            ...sec,
            points: sec.points.filter((p) => this.matchesSearch(p, q)),
          }))
          .filter((sec) => sec.points.length > 0),
        points: ch.points.filter((p) => this.matchesSearch(p, q)),
      }))
      .filter((ch) => ch.sections.length > 0);
  }

  isChapterExpanded(chapter: string): boolean {
    if (this.search.trim()) return true;
    return this.expandedChapters.has(chapter);
  }

  toggleChapter(chapter: string): void {
    if (this.expandedChapters.has(chapter)) {
      this.expandedChapters.delete(chapter);
    } else {
      this.expandedChapters.add(chapter);
      this.defaultExpandSectionsForChapter(chapter);
    }
    this.cdr.markForCheck();
  }

  expandAll(): void {
    for (const ch of this.chapterGroups) {
      this.expandedChapters.add(ch.chapter);
      for (const sec of ch.sections) {
        if (this.showSectionBar(ch.sections, sec.key, ch.chapter)) {
          this.expandedSections.add(this.sectionId(ch.chapter, sec.key));
        }
      }
    }
    this.cdr.markForCheck();
  }

  collapseAll(): void {
    this.search = '';
    this.expandedChapters.clear();
    this.expandedSections.clear();
    this.cdr.markForCheck();
  }

  expandAllDetails(): void {
    // Only expand details for currently expanded chapters — full catalog expand freezes the UI.
    for (const ch of this.chapterGroups) {
      if (!this.isChapterExpanded(ch.chapter)) continue;
      for (const sec of ch.sections) {
        if (
          this.showSectionBar(ch.sections, sec.key, ch.chapter) &&
          !this.isSectionExpanded(ch.chapter, sec.key)
        ) {
          continue;
        }
        for (const p of sec.points) {
          if (this.showDetail(p)) this.expandedPoints.add(p.point_id);
        }
      }
    }
    this.cdr.markForCheck();
  }

  collapseAllDetails(): void {
    this.expandedPoints.clear();
    this.cdr.markForCheck();
  }

  sectionId(chapter: string, sectionKey: string): string {
    return `${chapter}::${sectionKey}`;
  }

  isSectionExpanded(chapter: string, sectionKey: string): boolean {
    if (this.search.trim()) return true;
    return this.expandedSections.has(this.sectionId(chapter, sectionKey));
  }

  toggleSection(chapter: string, sectionKey: string, event?: Event): void {
    event?.stopPropagation();
    const id = this.sectionId(chapter, sectionKey);
    if (this.expandedSections.has(id)) this.expandedSections.delete(id);
    else this.expandedSections.add(id);
    this.cdr.markForCheck();
  }

  get canExpandCollapse(): boolean {
    return !this.loading && !this.building && this.chapterGroups.length > 0;
  }

  onSearchChange(value: string): void {
    this.search = value;
    this.cdr.markForCheck();
  }

  showSectionBar(sections: GovPointChapterGroup['sections'], key: string, chapter: string): boolean {
    return sections.length > 1 || key !== chapter;
  }

  sectionLabel(sec: { key: string; points: GovPoint[] }): string {
    return formatSectionGroupLabel(
      sec.key,
      sectionHeadingTitleForKey(sec.key, this.allGovPoints.length ? this.allGovPoints : sec.points),
    );
  }

  chapterLabel(chapter: string): string {
    return formatChapterLabel(
      chapter,
      sectionHeadingTitleForKey(chapter, this.allGovPoints),
    );
  }

  truncate(text: string, max: number): string {
    const t = text.trim();
    if (t.length <= max) return t;
    return `${t.slice(0, max)}…`;
  }

  hasSeparateTitle(p: GovPoint): boolean {
    const title = (p.title ?? '').trim();
    const text = (p.text ?? '').trim();
    return !!(title && text && title !== text);
  }

  pointTitle(p: GovPoint): string {
    return (p.title ?? '').trim();
  }

  pointDetail(p: GovPoint): string {
    const title = (p.title ?? '').trim();
    const text = (p.text ?? '').trim();
    if (title && text && title !== text) return text;
    if (!title && text) return text;
    return '';
  }

  pointDetailParagraphs(p: GovPoint): string[] {
    const text = this.pointDetail(p).trim();
    if (!text) return [];
    const byBlank = text.split(/\n\s*\n+/).map((s) => s.trim()).filter(Boolean);
    if (byBlank.length > 1) return byBlank;
    const byLine = text.split(/\n+/).map((s) => s.trim()).filter(Boolean);
    if (byLine.length > 4) return byLine;
    return [text];
  }

  showDetail(p: GovPoint): boolean {
    return this.pointDetail(p).length > 0;
  }

  isDetailLong(p: GovPoint): boolean {
    return this.pointDetail(p).length > this.previewLen;
  }

  isPointExpanded(pointId: string): boolean {
    if (this.search.trim()) return true;
    return this.expandedPoints.has(pointId);
  }

  togglePointText(pointId: string, event?: Event): void {
    event?.stopPropagation();
    if (this.expandedPoints.has(pointId)) {
      this.expandedPoints.delete(pointId);
    } else {
      this.expandedPoints.add(pointId);
    }
    this.cdr.markForCheck();
  }

  isComparablePoint(pointId: string): boolean {
    return !this.skippedReasons.has(pointId.trim());
  }

  skipReason(pointId: string): string | null {
    return this.skippedReasons.get(pointId.trim()) ?? null;
  }

  skipReasonShort(pointId: string): string {
    const reason = this.skipReason(pointId);
    if (!reason) return '';
    if (reason.includes('§1')) return 'Intro — not in gap analysis';
    if (reason.includes('introduction')) return 'Intro — not in gap analysis';
    if (reason.includes('annex')) return 'Annex — not in gap analysis';
    if (reason.includes('section header')) return 'Section header only';
    if (reason.includes('informational')) return 'Informational only';
    return reason.length > 48 ? `${reason.slice(0, 45)}…` : reason;
  }

  isHighlighted(pointId: string): boolean {
    const h = this.highlightPointNumber.trim();
    if (!h) return false;
    return pointId.trim().toLowerCase() === h.toLowerCase();
  }

  pointPageLabel(pointId: string): string | null {
    const p = this.pointMeta(pointId);
    if (!p) return null;
    return formatPointPageRef(p.pageReference, resolveRegulationPdfPage(p.pageReference, p.pdfPage ?? null));
  }

  openPointPage(pointId: string, event: Event): void {
    event.stopPropagation();
    const p = this.pointMeta(pointId);
    if (!p) return;
    const page = resolveRegulationPdfPage(p.pageReference, p.pdfPage ?? null);
    if (page) this.openSourcePage.emit(page);
  }

  private rebuildGroups(highlight = ''): void {
    const displayPoints = filterRegulationPointsForDisplay(this.points);
    this.pointsByNumber = new Map(
      displayPoints.map((p) => [p.pointNumber.trim().toLowerCase(), p]),
    );
    const stats = computeRegulationPointStats(displayPoints, this.docName, this.reportedPointCount);
    const rawGovPoints = sortByPointRef(
      displayPoints.map((p) => regulationPointToGovPoint(p)),
      (p) => p.point_id || p.section || '',
    );
    this.allGovPoints = buildGovPointDisplayCatalog(rawGovPoints);
    this.storedCount = stats.storedCount;
    this.analyseCount = stats.analyseCount;
    this.skippedCount = stats.skippedCount;
    const skippedIntroIds = new Set(
      stats.skipped
        .filter((s) => s.reason.includes('§1') || s.reason.includes('introduction'))
        .map((s) => s.point.point_id.trim().toLowerCase()),
    );
    this.introductionCount = displayPoints.filter(
      (p) => p.isIntroductionPoint || skippedIntroIds.has(p.pointNumber.trim().toLowerCase()),
    ).length;
    this.skippedReasons = new Map(
      stats.skipped.map((s) => [s.point.point_id.trim(), s.reason]),
    );
    this.chapterGroups = groupGovPointsForPicker(rawGovPoints);
    this.expandedChapters.clear();
    this.expandedSections.clear();
    this.expandedPoints.clear();

    if (highlight) {
      const hl = highlight.toLowerCase();
      for (const ch of this.chapterGroups) {
        for (const sec of ch.sections) {
          for (const p of sec.points) {
            if (p.point_id.trim().toLowerCase() !== hl) continue;
            this.expandedChapters.add(ch.chapter);
            if (this.showSectionBar(ch.sections, sec.key, ch.chapter)) {
              this.expandedSections.add(this.sectionId(ch.chapter, sec.key));
            }
            this.expandedPoints.add(p.point_id);
          }
        }
      }
      return;
    }

    if (!this.chapterGroups.length) return;

    // Everything open by default. Very large catalogs render thousands of full texts at
    // once and freeze the tab, so those fall back to the first chapter with details closed.
    if (rawGovPoints.length <= MaxAutoExpandPoints) {
      this.expandAll();
      this.expandAllDetails();
      return;
    }

    const first = this.chapterGroups[0];
    this.expandedChapters.add(first.chapter);
    this.defaultExpandSectionsForChapter(first.chapter);
  }

  private defaultExpandSectionsForChapter(chapter: string): void {
    const ch = this.chapterGroups.find((g) => g.chapter === chapter);
    if (!ch) return;
    for (const sec of ch.sections) {
      if (this.showSectionBar(ch.sections, sec.key, ch.chapter)) {
        this.expandedSections.add(this.sectionId(chapter, sec.key));
      }
    }
  }

  pointMeta(pointId: string): RegulationPoint | undefined {
    return this.pointsByNumber.get(pointId.trim().toLowerCase());
  }

  private matchesSearch(p: GovPoint, q: string): boolean {
    const hay = `${p.point_id} ${p.title ?? ''} ${p.text} ${p.section ?? ''}`.toLowerCase();
    return hay.includes(q);
  }
}
