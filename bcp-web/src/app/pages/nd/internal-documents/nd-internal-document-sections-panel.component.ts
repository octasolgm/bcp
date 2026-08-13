import { Component, EventEmitter, Input, OnChanges, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import type { InternalDocumentSection } from '../../../../lib/nd/types';
import { sortInternalSectionsByPointRef, normalizeInternalSectionRef } from '../../../../lib/nd/internal-section-group';

@Component({
  selector: 'app-nd-internal-document-sections-panel',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './nd-internal-document-sections-panel.component.html',
  styleUrl: './nd-internal-document-sections-panel.component.scss',
})
export class NdInternalDocumentSectionsPanelComponent implements OnChanges {
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
  @Output() openSourcePage = new EventEmitter<number>();

  search = '';
  expandedRows = new Set<string>();
  sortedSections: InternalDocumentSection[] = [];
  readonly previewLen = 160;

  readonly formatSectionRef = normalizeInternalSectionRef;

  ngOnChanges(): void {
    this.sortedSections = sortInternalSectionsByPointRef(this.sections);
    this.expandedRows.clear();
    if (this.sortedSections.length > 0) {
      this.expandAll();
    }
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

  expandAll(): void {
    for (const section of this.sortedSections) {
      this.expandedRows.add(section.id);
    }
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

  preview(text: string): string {
    if (text.length <= this.previewLen) return text;
    return text.slice(0, this.previewLen).trimEnd() + '…';
  }

  sectionParagraphs(text: string): string[] {
    const t = text.trim();
    if (!t) return [];
    const byBlank = t.split(/\n\s*\n+/).map((s) => s.trim()).filter(Boolean);
    if (byBlank.length > 1) return byBlank;
    const byLine = t.split(/\n+/).map((s) => s.trim()).filter(Boolean);
    if (byLine.length > 4) return byLine;
    return [t];
  }

  openPage(page: number | null | undefined, event: Event): void {
    event.stopPropagation();
    if (!this.canOpenSource || !page || page < 1) return;
    this.openSourcePage.emit(page);
  }

  private matchesSearch(section: InternalDocumentSection, q: string): boolean {
    const displayRef = normalizeInternalSectionRef(section.sectionRef);
    const hay = `${displayRef} ${section.sectionRef} ${section.sectionText} ${section.sourcePage ?? ''}`.toLowerCase();
    return hay.includes(q);
  }
}
