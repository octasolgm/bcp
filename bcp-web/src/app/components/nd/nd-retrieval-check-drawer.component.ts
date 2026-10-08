import { Component, EventEmitter, HostListener, Input, OnInit, Output, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  NdApiService,
  NdRetrievalCheckClause,
  NdRetrievalCheckResult,
  NdRetrievalCheckSnippet,
} from '../../services/nd/nd-api.service';

/**
 * Right-side drawer on an analysis report (platform super admins, real accounts): the free retrieval check.
 * For each clause, a reviewer lists short snippets of internal-document text the clause should retrieve; the
 * check runs Steps 1-6 only (current pipeline version, this analysis's documents, no AI call) and shows, per
 * snippet, whether a selected section / passage contains it. Confirms retrieval before paying for a judgment run.
 */
@Component({
  selector: 'app-nd-retrieval-check-drawer',
  standalone: true,
  imports: [FormsModule],
  templateUrl: './nd-retrieval-check-drawer.component.html',
  styleUrl: './nd-retrieval-check-drawer.component.scss',
})
export class NdRetrievalCheckDrawerComponent implements OnInit {
  private readonly ndApi = inject(NdApiService);

  @Input({ required: true }) runId!: string;
  @Input() runName = '';
  @Output() closed = new EventEmitter<void>();

  loading = false;
  running = false;
  error = '';
  pipelineLabel = '';
  clauses: NdRetrievalCheckClause[] = [];
  /** Snippet text per clause key, one snippet per line. */
  drafts: Record<string, string> = {};
  readonly checked = new Set<string>();
  readonly savingKeys = new Set<string>();
  readonly savedKeys = new Set<string>();
  results: Record<string, NdRetrievalCheckResult> = {};

  async ngOnInit(): Promise<void> {
    this.loading = true;
    const res = await this.ndApi.getRetrievalCheckSetup(this.runId);
    this.loading = false;
    if (!res.success || !res.data) {
      this.error = res.message ?? 'Could not load this analysis.';
      return;
    }
    this.pipelineLabel = res.data.pipelineLabel;
    this.clauses = res.data.clauses;
    for (const c of this.clauses) {
      this.drafts[c.clauseKey] = c.expected.join('\n');
      if (c.expected.length) this.checked.add(c.clauseNo);
    }
  }

  @HostListener('document:keydown.escape')
  onEscape(): void {
    this.closed.emit();
  }

  toggle(clauseNo: string, on: boolean): void {
    if (on) this.checked.add(clauseNo);
    else this.checked.delete(clauseNo);
  }

  snippetsOf(clause: NdRetrievalCheckClause): string[] {
    return (this.drafts[clause.clauseKey] ?? '')
      .split('\n')
      .map((l) => l.trim())
      .filter((l) => l.length > 0);
  }

  isDirty(clause: NdRetrievalCheckClause): boolean {
    return this.snippetsOf(clause).join('\n') !== clause.expected.join('\n');
  }

  async save(clause: NdRetrievalCheckClause): Promise<boolean> {
    this.savingKeys.add(clause.clauseKey);
    const res = await this.ndApi.saveRetrievalExpectation({
      clauseKey: clause.clauseKey,
      clauseNo: clause.clauseNo,
      expected: this.snippetsOf(clause),
    });
    this.savingKeys.delete(clause.clauseKey);
    if (!res.success || !res.data) {
      this.error = res.message ?? 'Could not save the expected evidence.';
      return false;
    }
    clause.expected = res.data.expected;
    this.savedKeys.add(clause.clauseKey);
    return true;
  }

  async run(): Promise<void> {
    if (!this.checked.size || this.running) return;
    this.error = '';
    // Unsaved snippet edits are saved first, so the check uses what is on screen.
    for (const clause of this.clauses) {
      if (this.checked.has(clause.clauseNo) && this.isDirty(clause) && !(await this.save(clause))) return;
    }
    this.running = true;
    const res = await this.ndApi.runRetrievalCheck(this.runId, [...this.checked]);
    this.running = false;
    if (!res.success || !res.data) {
      this.error = res.message ?? 'The retrieval check failed.';
      return;
    }
    for (const r of res.data.clauses) this.results[r.clauseKey] = r;
  }

  snippetState(s: NdRetrievalCheckSnippet): 'selected' | 'missed' | 'absent' {
    if (s.selected) return 'selected';
    return s.foundIn.length ? 'missed' : 'absent';
  }

  formatNumber(n: number): string {
    return n.toLocaleString();
  }

  seconds(ms: number): string {
    return (ms / 1000).toFixed(1);
  }
}
