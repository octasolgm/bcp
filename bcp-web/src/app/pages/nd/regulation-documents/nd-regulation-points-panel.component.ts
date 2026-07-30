import { Component, Input, OnChanges } from '@angular/core';
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
import {
  analyzeGovPointSet,
  formatPointCountSummary,
} from '../../../../lib/library-points-utils';
import { regulationPointToGovPoint } from '../../../../lib/regulation-catalog-utils';
import type { RegulationPoint } from '../../../../lib/nd/types';

@Component({
  selector: 'app-nd-regulation-points-panel',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './nd-regulation-points-panel.component.html',
  styleUrl: './nd-regulation-points-panel.component.scss',
})
export class NdRegulationPointsPanelComponent implements OnChanges {
  @Input() docName = '';
  @Input() points: RegulationPoint[] = [];
  @Input() source = '';
  @Input() loading = false;
  @Input() highlightPointNumber = '';

  search = '';
  expandedChapters = new Set<string>();
  expandedSections = new Set<string>();
  expandedPoints = new Set<string>();
  chapterGroups: GovPointChapterGroup[] = [];
  private allGovPoints: GovPoint[] = [];
  readonly previewLen = 120;
  storedCount = 0;
  analyseCount = 0;
  skippedCount = 0;
  skippedReasons = new Map<string, string>();
  introductionCount = 0;

  readonly formatGovPointDisplayId = formatGovPointDisplayId;
  readonly formatSectionGroupLabel = formatSectionGroupLabel;
  readonly formatChapterLabel = formatChapterLabel;

  ngOnChanges(): void {
    const highlight = this.highlightPointNumber.trim();
    if (highlight) {
      this.search = highlight;
      this.expandedChapters.clear();
    }
    this.rebuildGroups();
    if (highlight) {
      for (const ch of this.chapterGroups) {
        this.expandedChapters.add(ch.chapter);
        for (const sec of ch.sections) {
          if (this.showSectionBar(ch.sections, sec.key, ch.chapter)) {
            this.expandedSections.add(this.sectionId(ch.chapter, sec.key));
          }
          for (const p of sec.points) {
            if (p.point_id.trim().toLowerCase() === highlight.toLowerCase()) {
              this.expandedPoints.add(p.point_id);
            }
          }
        }
      }
    }
  }

  get statusLabel(): string {
    if (this.loading) return 'Loading points…';
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
  }

  collapseAll(): void {
    this.search = '';
    this.expandedChapters.clear();
    this.expandedSections.clear();
  }

  expandAllDetails(): void {
    for (const ch of this.chapterGroups) {
      for (const sec of ch.sections) {
        for (const p of sec.points) {
          if (this.isDetailLong(p)) {
            this.expandedPoints.add(p.point_id);
          }
        }
      }
    }
  }

  collapseAllDetails(): void {
    this.expandedPoints.clear();
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
  }

  get canExpandCollapse(): boolean {
    return !this.loading && this.chapterGroups.length > 0;
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

  private rebuildGroups(): void {
    const rawGovPoints = this.points.map((p) => regulationPointToGovPoint(p));
    this.allGovPoints = buildGovPointDisplayCatalog(rawGovPoints);
    const analyzed = analyzeGovPointSet(rawGovPoints, { docName: this.docName });
    this.storedCount = analyzed.storedCount;
    this.analyseCount = analyzed.analyseCount;
    this.skippedCount = analyzed.skippedCount;
    this.introductionCount = this.points.filter(
      (p) =>
        p.isIntroductionPoint ||
        analyzed.skipped.some(
          (s) =>
            s.point.point_id.trim() === p.pointNumber.trim() &&
            (s.reason.includes('§1') || s.reason.includes('introduction')),
        ),
    ).length;
    this.skippedReasons = new Map(
      analyzed.skipped.map((s) => [s.point.point_id.trim(), s.reason]),
    );
    this.chapterGroups = groupGovPointsForPicker(rawGovPoints);
    this.expandedChapters.clear();
    this.expandedSections.clear();
    this.expandedPoints.clear();
    if (this.chapterGroups.length) {
      for (const ch of this.chapterGroups) {
        if (ch.chapter === '1' || ch.chapter === 'intro') {
          this.expandedChapters.add(ch.chapter);
        }
      }
      const first = this.chapterGroups[0];
      this.expandedChapters.add(first.chapter);
      for (const sec of first.sections) {
        if (this.showSectionBar(first.sections, sec.key, first.chapter)) {
          this.expandedSections.add(this.sectionId(first.chapter, sec.key));
        }
      }
    }
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
    return this.points.find((p) => p.pointNumber.trim() === pointId.trim());
  }

  private matchesSearch(p: GovPoint, q: string): boolean {
    const hay = `${p.point_id} ${p.title ?? ''} ${p.text} ${p.section ?? ''}`.toLowerCase();
    return hay.includes(q);
  }
}
