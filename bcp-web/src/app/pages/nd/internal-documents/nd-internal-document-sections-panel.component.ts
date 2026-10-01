import {
  Component,
  ElementRef,
  EventEmitter,
  Input,
  OnChanges,
  Output,
  QueryList,
  SimpleChanges,
  ViewChild,
  ViewChildren,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import type { InternalDocumentSection } from '../../../../lib/nd/types';
import { sortInternalSectionsByPointRef, normalizeInternalSectionRef } from '../../../../lib/nd/internal-section-group';
import { splitSectionDisplayText } from '../../../../lib/nd/clause-section-display';
import { sanitizePolicySectionText } from '../../../../lib/nd/policy-section-text';

@Component({
  selector: 'app-nd-internal-document-sections-panel',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './nd-internal-document-sections-panel.component.html',
  styleUrl: './nd-internal-document-sections-panel.component.scss',
})
export class NdInternalDocumentSectionsPanelComponent implements OnChanges {
  @ViewChild('panelScroll') private panelScroll?: ElementRef<HTMLElement>;
  @ViewChildren('sectionCard') private sectionCards?: QueryList<ElementRef<HTMLElement>>;

  @Input() docTitle = '';
  @Input() sections: InternalDocumentSection[] = [];
  @Input() loading = false;
  @Input() extracting = false;
  @Input() extractingLabel = '';
  @Input() extractingPct: number | null = null;
  @Input() repairing = false;
  @Input() repairingLabel = '';
  @Input() repairingPct: number | null = null;
  @Input() canOpenSource = false;
  /** Pre-fills the panel search once sections load, so a deep link lands on the matching passage. */
  @Input() initialSearch = '';
  @Output() openSourcePage = new EventEmitter<number>();
  private initialSearchApplied = false;

  search = '';
  expandedRows = new Set<string>();
  sortedSections: InternalDocumentSection[] = [];
  readonly previewLen = 160;

  readonly formatSectionRef = normalizeInternalSectionRef;

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['initialSearch']) this.initialSearchApplied = false;
    if (!changes['sections']) return;
    this.sortedSections = sortInternalSectionsByPointRef(this.sections ?? []);
    this.expandedRows.clear();
    this.expandAll();
    this.applyInitialSearch();
  }

  /** Narrows to the passage: the full quote when a section contains it, else its opening words. */
  private applyInitialSearch(): void {
    const find = this.initialSearch.trim();
    if (this.initialSearchApplied || !find || !this.sortedSections.length) return;
    this.initialSearchApplied = true;
    const lower = find.toLowerCase();
    const candidates = [lower, lower.split(/\s+/).slice(0, 8).join(' '), lower.split(/\s+/).slice(0, 4).join(' ')];
    this.search =
      candidates.find((c) => c && this.sortedSections.some((s) => this.matchesSearch(s, c))) ?? '';
  }

  get isBusy(): boolean {
    return this.extracting || this.repairing;
  }

  get busyTitle(): string {
    if (this.extracting) return this.extractingLabel.trim() || 'Extracting sections…';
    if (this.repairing) return this.repairingLabel.trim() || 'Repairing page references…';
    return '';
  }

  get busyHint(): string | null {
    if (this.extracting) {
      const label = this.extractingLabel.toLowerCase();
      if (label.includes('page')) {
        return 'Assigning PDF pages — included in extract, no separate repair needed.';
      }
      return 'Landing AI extract can take several minutes for large manuals.';
    }
    if (this.repairing) return 'Large manuals may take several minutes — keep this tab open.';
    return null;
  }

  get statusLabel(): string {
    if (this.extracting) return this.busyTitle;
    if (this.repairing) return this.busyTitle;
    if (this.loading) return 'Loading sections…';
    if (!this.sections.length) return 'No sections extracted yet.';
    return `${this.sections.length} policy section${this.sections.length === 1 ? '' : 's'} extracted`;
  }

  get visibleSections(): InternalDocumentSection[] {
    const q = this.search.trim().toLowerCase();
    if (!q) return this.sortedSections;
    return this.sortedSections.filter((section) => this.matchesSearch(section, q));
  }

  get canExpandDetails(): boolean {
    return !this.loading && !this.isBusy && this.sortedSections.length > 0;
  }

  isRowExpanded(id: string): boolean {
    if (this.search.trim()) return true;
    return this.expandedRows.has(id);
  }

  toggleRow(id: string): void {
    if (this.expandedRows.has(id)) this.expandedRows.delete(id);
    else this.expandedRows.add(id);
  }

  rowKey(section: InternalDocumentSection, index: number): string {
    return section.id || `${section.sectionRef}-${index}`;
  }

  expandAll(): void {
    this.sortedSections.forEach((section, index) => {
      this.expandedRows.add(this.rowKey(section, index));
    });
  }

  collapseAll(): void {
    this.search = '';
    this.expandedRows.clear();
  }

  expandAllDetails(): void {
    this.expandAll();
  }

  collapseAllDetails(): void {
    this.expandedRows.clear();
  }

  isLong(text: string): boolean {
    return text.length > this.previewLen;
  }

  sectionHeader(section: InternalDocumentSection): string {
    return splitSectionDisplayText(section.sectionText, section.sectionRef).header;
  }

  preview(section: InternalDocumentSection): string {
    const body = splitSectionDisplayText(section.sectionText, section.sectionRef).body;
    if (body.length <= this.previewLen) return body;
    return body.slice(0, this.previewLen).trimEnd() + '…';
  }

  sectionParagraphs(section: InternalDocumentSection): string[] {
    const t = splitSectionDisplayText(section.sectionText, section.sectionRef).body;
    if (!t) return [];
    const byBlank = t.split(/\n\s*\n+/).map((s) => s.trim()).filter(Boolean);
    if (byBlank.length > 1) return byBlank;
    const byLine = t.split(/\n+/).map((s) => s.trim()).filter(Boolean);
    if (byLine.length > 4) return byLine;
    return [t];
  }

  sectionPage(section: InternalDocumentSection): number | null {
    const page = section.sourcePage;
    return page != null && page > 0 ? page : null;
  }

  openPage(page: number | null | undefined, event: Event): void {
    event.stopPropagation();
    if (!this.canOpenSource || page == null || page < 1) return;
    this.openSourcePage.emit(page);
  }

  showSectionNav(): boolean {
    return this.visibleSections.length > 1;
  }

  goToSectionNav(visibleIndex: number, event: Event): void {
    event.stopPropagation();
    if (visibleIndex < 0 || visibleIndex >= this.visibleSections.length) return;
    const section = this.visibleSections[visibleIndex];
    const sortedIdx = this.sortedSections.indexOf(section);
    const key = this.rowKey(section, sortedIdx >= 0 ? sortedIdx : visibleIndex);
    this.expandedRows.add(key);
    queueMicrotask(() => {
      const cards = this.sectionCards?.toArray() ?? [];
      const el = cards[visibleIndex]?.nativeElement;
      el?.scrollIntoView({ block: 'start', behavior: 'smooth' });
    });
  }

  private matchesSearch(section: InternalDocumentSection, q: string): boolean {
    const displayRef = normalizeInternalSectionRef(section.sectionRef);
    const hay = `${displayRef} ${section.sectionRef} ${section.sectionText} ${section.sourcePage ?? ''}`.toLowerCase();
    return hay.includes(q);
  }
}
