import { CommonModule } from '@angular/common';
import { Component, OnInit, inject } from '@angular/core';
import { NdPageAlertComponent } from '../../../components/nd/nd-page-alert.component';
import { NdApiService, NdLocalExtractionResult, NdLocalExtractionSection } from '../../../services/nd/nd-api.service';

type TextDoc = {
  id: string;
  title: string;
  fileName: string;
  uploadedAt: string | null;
  sizeBytes: number | null;
};

/** One numbered point, nested under its parent by numbering depth ("6" -> "6.2" -> "6.2.1"). */
type PointNode = {
  clauseNo: string;
  clauseText: string;
  sourcePage: number | null;
  children: PointNode[];
};

/**
 * A simple third document library — like Internal Documents / Regulation Documents but generic: no
 * department, no analysis-run linkage. Upload a PDF/Word file, Parse & Extract locally (same PdfPig +
 * Tesseract pipeline as the other two — see nd/local-documents), and view the result as a nested
 * Point / Sub-point tree instead of a flat clause list.
 */
@Component({
  selector: 'app-nd-text-documents',
  standalone: true,
  imports: [CommonModule, NdPageAlertComponent],
  templateUrl: './nd-text-documents.component.html',
  styleUrl: './nd-text-documents.component.scss',
})
export class NdTextDocumentsComponent implements OnInit {
  private readonly api = inject(NdApiService);

  docs: TextDoc[] = [];
  loading = false;
  uploading = false;
  message = '';
  error = '';

  parsing = new Set<string>();
  extracting = new Set<string>();
  results = new Map<string, NdLocalExtractionResult>();
  expanded = new Set<string>();
  /** Which point nodes are collapsed in the tree view, per document (id -> set of clauseNo). */
  private collapsedNodes = new Map<string, Set<string>>();

  ngOnInit(): void {
    void this.load();
  }

  async load(): Promise<void> {
    this.loading = true;
    this.error = '';
    try {
      const res = await this.api.getTextDocuments();
      if (!res.success) {
        this.error = res.message || 'Could not load documents.';
        this.docs = [];
        return;
      }
      const raw = (res.data ?? []) as Record<string, unknown>[];
      this.docs = raw.map((d) => ({
        id: String(d['id'] ?? ''),
        title: (d['title'] as string) || 'Untitled',
        fileName: (d['originalFileName'] as string) || '',
        uploadedAt: (d['uploadedAt'] as string) || null,
        sizeBytes: (d['sizeBytes'] as number) ?? null,
      }));
      await this.loadStatuses();
    } catch (err) {
      this.error = err instanceof Error ? err.message : 'Could not load documents.';
    } finally {
      this.loading = false;
    }
  }

  private async loadStatuses(): Promise<void> {
    const ids = this.docs.map((d) => d.id).filter(Boolean);
    if (ids.length === 0) return;
    try {
      const res = await this.api.localExtractStatusBatch(ids);
      if (!res.success || !res.data) return;
      for (const doc of this.docs) {
        const status = res.data[doc.id];
        if (status) this.results.set(doc.id, status);
      }
    } catch {
      // Nice-to-have on load — don't block the list on it.
    }
  }

  async onFileChange(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (!file) return;

    this.uploading = true;
    this.error = '';
    this.message = '';
    try {
      const res = await this.api.uploadTextDocument(file);
      if (!res.success) {
        this.error = res.message || 'Upload failed.';
        return;
      }
      this.message = `Uploaded ${file.name}.`;
      await this.load();
    } catch (err) {
      this.error = err instanceof Error ? err.message : 'Upload failed.';
    } finally {
      this.uploading = false;
    }
  }

  async handleDelete(doc: TextDoc): Promise<void> {
    if (!confirm(`Remove "${doc.title}" from the library?`)) return;
    this.error = '';
    const res = await this.api.hideTextDocument(doc.id);
    if (res.success) {
      this.docs = this.docs.filter((d) => d.id !== doc.id);
      this.results.delete(doc.id);
    } else {
      this.error = res.message || 'Failed to remove document.';
    }
  }

  async openDocument(doc: TextDoc): Promise<void> {
    const res = await this.api.getTextDocumentFileUrl(doc.id);
    if (res.success && res.data?.url) {
      window.open(res.data.url, '_blank', 'noopener');
      return;
    }
    this.error = res.message || 'Could not open document.';
  }

  /** Step 1 — convert the document to text/markdown with page references. Does not detect points. */
  async parse(doc: TextDoc): Promise<void> {
    this.parsing.add(doc.id);
    this.error = '';
    try {
      const res = await this.api.localParseById(doc.id);
      if (!res.success || !res.data) {
        this.error = res.message || `Local parse failed for "${doc.title}".`;
        return;
      }
      this.results.set(doc.id, res.data);
    } catch (err) {
      this.error = err instanceof Error ? err.message : `Local parse failed for "${doc.title}".`;
    } finally {
      this.parsing.delete(doc.id);
    }
  }

  /** Step 2 — split the already-parsed markdown into points. Requires Parse to have run first. */
  async extract(doc: TextDoc): Promise<void> {
    if (this.statusFor(doc.id) !== 'parsed' && this.extractStatusFor(doc.id) === 'pending') {
      this.error = `Parse "${doc.title}" first, then extract.`;
      return;
    }
    this.extracting.add(doc.id);
    this.error = '';
    try {
      const res = await this.api.localExtractById(doc.id);
      if (!res.success || !res.data) {
        this.error = res.message || `Local extraction failed for "${doc.title}".`;
        return;
      }
      this.results.set(doc.id, res.data);
      this.expanded.add(doc.id);
      console.log(`[local-extract] ${doc.title}`, res.data);
    } catch (err) {
      this.error = err instanceof Error ? err.message : `Local extraction failed for "${doc.title}".`;
    } finally {
      this.extracting.delete(doc.id);
    }
  }

  toggleExpanded(docId: string): void {
    if (this.expanded.has(docId)) this.expanded.delete(docId);
    else this.expanded.add(docId);
  }

  isParsing(docId: string): boolean {
    return this.parsing.has(docId);
  }

  isExtracting(docId: string): boolean {
    return this.extracting.has(docId);
  }

  isExpanded(docId: string): boolean {
    return this.expanded.has(docId);
  }

  resultFor(docId: string): NdLocalExtractionResult | undefined {
    return this.results.get(docId);
  }

  /** Parse status: pending | processing | parsed | failed. */
  statusFor(docId: string): string {
    return this.results.get(docId)?.status ?? 'pending';
  }

  /** Extract status: pending | processing | extracted | failed — independent of parse status. */
  extractStatusFor(docId: string): string {
    return this.results.get(docId)?.extractStatus ?? 'pending';
  }

  statusLabel(docId: string): string {
    switch (this.statusFor(docId)) {
      case 'parsed':
        return 'Parsed';
      case 'processing':
        return 'Processing…';
      case 'failed':
        return 'Failed';
      default:
        return 'Not parsed';
    }
  }

  statusClass(docId: string): string {
    const status = this.statusFor(docId);
    if (status === 'parsed') return 'doc-ready-green';
    if (status === 'failed') return 'doc-ready-red';
    return 'status-pending';
  }

  extractStatusLabel(docId: string): string {
    switch (this.extractStatusFor(docId)) {
      case 'extracted':
        return 'Extracted';
      case 'processing':
        return 'Processing…';
      case 'failed':
        return 'Failed';
      default:
        return 'Not extracted';
    }
  }

  extractStatusClass(docId: string): string {
    const status = this.extractStatusFor(docId);
    if (status === 'extracted') return 'doc-ready-green';
    if (status === 'failed') return 'doc-ready-red';
    return 'status-pending';
  }

  /** Build the Point / Sub-point tree from the flat clause list, nested by numbering depth. */
  pointTreeFor(docId: string): PointNode[] {
    const result = this.results.get(docId);
    if (!result) return [];
    return this.buildTree(result.sections);
  }

  private buildTree(sections: NdLocalExtractionSection[]): PointNode[] {
    const roots: PointNode[] = [];
    // Stack of the currently-open node at each depth, so "6.2.1" nests under "6.2" nests under "6".
    const stack: PointNode[] = [];

    for (const s of sections) {
      const node: PointNode = { clauseNo: s.clauseNo, clauseText: s.clauseText, sourcePage: s.sourcePage, children: [] };
      const depth = this.numberingDepth(s.clauseNo);

      while (stack.length >= depth && stack.length > 0) stack.pop();

      if (depth <= 1 || stack.length === 0) {
        roots.push(node);
      } else {
        stack[stack.length - 1].children.push(node);
      }
      stack.push(node);
    }
    return roots;
  }

  /** "6" -> 1, "6.2" -> 2, "6.2.1" -> 3. Non-numeric labels (e.g. "Introduction", "Article 12") count as depth 1. */
  private numberingDepth(clauseNo: string): number {
    const match = clauseNo.match(/^\d+(\.\d+)*$/);
    if (!match) return 1;
    return clauseNo.split('.').length;
  }

  isNodeCollapsed(docId: string, clauseNo: string): boolean {
    return this.collapsedNodes.get(docId)?.has(clauseNo) ?? false;
  }

  toggleNode(docId: string, clauseNo: string): void {
    let set = this.collapsedNodes.get(docId);
    if (!set) {
      set = new Set<string>();
      this.collapsedNodes.set(docId, set);
    }
    if (set.has(clauseNo)) set.delete(clauseNo);
    else set.add(clauseNo);
  }

  formatBytes(bytes: number | null): string {
    if (!bytes) return '';
    const kb = bytes / 1024;
    return kb > 1024 ? `${(kb / 1024).toFixed(1)} MB` : `${kb.toFixed(0)} KB`;
  }
}
