import { Component, Input, OnChanges } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import type { InternalDocumentSection } from '../../../../lib/nd/types';

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

  search = '';
  expanded = new Set<string>();
  readonly previewLen = 160;

  ngOnChanges(): void {
    this.expanded.clear();
  }

  get statusLabel(): string {
    if (this.loading) return 'Loading sections…';
    if (!this.sections.length) return 'No sections extracted yet.';
    return `${this.sections.length} policy section${this.sections.length === 1 ? '' : 's'} extracted`;
  }

  get visibleSections(): InternalDocumentSection[] {
    const q = this.search.trim().toLowerCase();
    if (!q) return this.sections;
    return this.sections.filter(
      (s) =>
        s.sectionRef.toLowerCase().includes(q) ||
        s.sectionText.toLowerCase().includes(q) ||
        String(s.sourcePage ?? '').includes(q),
    );
  }

  isExpanded(id: string): boolean {
    return this.expanded.has(id);
  }

  toggleExpanded(id: string): void {
    if (this.expanded.has(id)) this.expanded.delete(id);
    else this.expanded.add(id);
  }

  isLong(text: string): boolean {
    return text.length > this.previewLen;
  }

  preview(text: string): string {
    if (text.length <= this.previewLen) return text;
    return text.slice(0, this.previewLen).trimEnd() + '…';
  }
}
