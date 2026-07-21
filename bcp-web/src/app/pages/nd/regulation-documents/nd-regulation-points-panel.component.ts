import { Component, Input, OnChanges } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import {
  formatChapterLabel,
  formatGovPointDisplayId,
  formatSectionGroupLabel,
  groupGovPointsByChapter,
  type GovPoint,
  type GovPointChapterGroup,
} from '../../../../lib/gov-point-filter';
import {
  analyzeGovPointSet,
  formatPointCountSummary,
} from '../../../../lib/library-points-utils';
import type { RegulationPoint } from '../../../../lib/nd/types';

function toGovPoint(p: RegulationPoint): GovPoint {
  return {
    point_id: p.pointNumber,
    title: p.pointTitle ?? undefined,
    text: p.pointContent,
    section: p.pageReference ?? undefined,
  };
}

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
  chapterGroups: GovPointChapterGroup[] = [];
  storedCount = 0;
  analyseCount = 0;
  skippedCount = 0;

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
      }
    }
  }

  get statusLabel(): string {
    if (this.loading) return 'Loading points…';
    if (!this.storedCount) return 'No points extracted yet.';
    return formatPointCountSummary({
      storedCount: this.storedCount,
      analyseCount: this.analyseCount,
      skippedCount: this.skippedCount,
      comparable: [],
      skipped: [],
    });
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
    if (this.expandedChapters.has(chapter)) this.expandedChapters.delete(chapter);
    else this.expandedChapters.add(chapter);
  }

  showSectionBar(sections: GovPointChapterGroup['sections'], key: string, chapter: string): boolean {
    return sections.length > 1 || key !== chapter;
  }

  truncate(text: string, max: number): string {
    const t = text.trim();
    if (t.length <= max) return t;
    return `${t.slice(0, max)}…`;
  }

  isHighlighted(pointId: string): boolean {
    const h = this.highlightPointNumber.trim();
    if (!h) return false;
    return pointId.trim().toLowerCase() === h.toLowerCase();
  }

  private rebuildGroups(): void {
    const govPoints = this.points.map(toGovPoint);
    const analyzed = analyzeGovPointSet(govPoints, { docName: this.docName });
    this.storedCount = analyzed.storedCount;
    this.analyseCount = analyzed.analyseCount;
    this.skippedCount = analyzed.skippedCount;
    this.chapterGroups = groupGovPointsByChapter(analyzed.comparable);
    this.expandedChapters.clear();
    if (this.chapterGroups.length) {
      this.expandedChapters.add(this.chapterGroups[0].chapter);
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
