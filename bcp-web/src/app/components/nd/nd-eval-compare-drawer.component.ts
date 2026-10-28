import { Component, EventEmitter, HostListener, Input, OnInit, Output, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import {
  NdApiService,
  NdClauseEvalSummary,
  NdEvalClauseComparison,
  NdEvalComparison,
  NdRunEvalClause,
} from '../../services/nd/nd-api.service';

export type NdEvalDrawerMode = 'compare' | 'save';

/**
 * Right-side drawer on an analysis report.
 * - Save as eval: tick clauses of this analysis; each is saved as the next version of that clause's eval
 *   (current by default).
 * - Compare: this analysis's clauses on the left, current clause evals on the right, tick both sides and
 *   compare. The comparison is local and rule-based (no AI call, no cost).
 */
@Component({
  selector: 'app-nd-eval-compare-drawer',
  standalone: true,
  imports: [FormsModule, RouterLink],
  templateUrl: './nd-eval-compare-drawer.component.html',
  styleUrl: './nd-eval-compare-drawer.component.scss',
})
export class NdEvalCompareDrawerComponent implements OnInit {
  private readonly ndApi = inject(NdApiService);

  @Input({ required: true }) runId!: string;
  @Input() runName = '';
  @Input() mode: NdEvalDrawerMode = 'compare';
  @Output() closed = new EventEmitter<void>();

  loading = false;
  error = '';
  runClauses: NdRunEvalClause[] = [];
  currentEvals: NdClauseEvalSummary[] = [];

  /** Left column (this analysis) and right column (current evals) selections. */
  readonly leftChecked = new Set<string>();
  readonly rightChecked = new Set<string>();

  comparing = false;
  comparison: NdEvalComparison | null = null;
  filter: 'all' | 'changed' = 'all';
  private readonly open = new Set<string>();

  /** Save tab. */
  readonly saveChecked = new Set<string>();
  saveNotes = '';
  saveAsCurrent = true;
  saving = false;
  savedMessage = '';

  async ngOnInit(): Promise<void> {
    await this.load();
  }

  @HostListener('document:keydown.escape')
  onEscape(): void {
    this.closed.emit();
  }

  setMode(mode: NdEvalDrawerMode): void {
    this.mode = mode;
    this.error = '';
    this.savedMessage = '';
  }

  private async load(): Promise<void> {
    this.loading = true;
    const [clausesRes, evalsRes] = await Promise.all([
      this.ndApi.getRunEvalClauses(this.runId),
      this.ndApi.listEvals(),
    ]);
    this.loading = false;
    if (!clausesRes.success || !clausesRes.data) {
      this.error = clausesRes.message ?? 'Could not load this analysis.';
      return;
    }
    this.runClauses = clausesRes.data.clauses;
    this.currentEvals = (evalsRes.success ? evalsRes.data ?? [] : []).filter((e) => e.isCurrent);
    this.preselect();
  }

  /** Every judged clause that has a current eval is ticked on both sides, so Compare works in one click. */
  private preselect(): void {
    this.leftChecked.clear();
    this.rightChecked.clear();
    for (const c of this.runClauses) {
      const match = this.evalFor(c);
      if (match && c.overallStatus) {
        this.leftChecked.add(c.clauseNo);
        this.rightChecked.add(match.id);
      }
    }
  }

  /** The current eval for a clause: same regulation and number first, then the same number. */
  evalFor(c: NdRunEvalClause): NdClauseEvalSummary | undefined {
    return (
      this.currentEvals.find((e) => e.clauseKey === c.clauseKey) ??
      this.currentEvals.find((e) => normalizeNo(e.clauseNo) === normalizeNo(c.clauseNo))
    );
  }

  hasEval(c: NdRunEvalClause): boolean {
    return !!this.evalFor(c);
  }

  toggleLeft(c: NdRunEvalClause): void {
    if (this.leftChecked.has(c.clauseNo)) {
      this.leftChecked.delete(c.clauseNo);
      return;
    }
    this.leftChecked.add(c.clauseNo);
    // Ticking a clause also ticks its current eval, the usual pairing.
    const match = this.evalFor(c);
    if (match) this.rightChecked.add(match.id);
  }

  toggleRight(e: NdClauseEvalSummary): void {
    if (this.rightChecked.has(e.id)) this.rightChecked.delete(e.id);
    else this.rightChecked.add(e.id);
  }

  setAllLeft(on: boolean): void {
    this.leftChecked.clear();
    if (on) this.runClauses.forEach((c) => this.leftChecked.add(c.clauseNo));
  }

  setAllRight(on: boolean): void {
    this.rightChecked.clear();
    if (on) this.currentEvals.forEach((e) => this.rightChecked.add(e.id));
  }

  get canCompare(): boolean {
    return this.leftChecked.size > 0 && this.rightChecked.size > 0 && !this.comparing;
  }

  async compare(): Promise<void> {
    this.comparing = true;
    this.error = '';
    const res = await this.ndApi.compareClausesWithEvals({
      runId: this.runId,
      clauseNos: [...this.leftChecked],
      evalIds: [...this.rightChecked],
    });
    this.comparing = false;
    if (!res.success || !res.data) {
      this.comparison = null;
      this.error = res.message ?? 'Could not compare.';
      return;
    }
    this.comparison = res.data;
    this.open.clear();
  }

  // ---- save tab

  toggleSave(c: NdRunEvalClause): void {
    if (!c.overallStatus) return;
    if (this.saveChecked.has(c.clauseNo)) this.saveChecked.delete(c.clauseNo);
    else this.saveChecked.add(c.clauseNo);
  }

  setAllSave(on: boolean): void {
    this.saveChecked.clear();
    if (on) this.runClauses.filter((c) => c.overallStatus).forEach((c) => this.saveChecked.add(c.clauseNo));
  }

  /** Version the clause would get if saved now. */
  nextVersion(c: NdRunEvalClause): number {
    const current = this.evalFor(c);
    return current ? current.versionNumber + 1 : 1;
  }

  async save(): Promise<void> {
    if (this.saveChecked.size === 0) {
      this.error = 'Tick at least one clause to save.';
      return;
    }
    this.saving = true;
    this.error = '';
    this.savedMessage = '';
    const res = await this.ndApi.saveClauseEvals({
      runId: this.runId,
      clauseNos: [...this.saveChecked],
      notes: this.saveNotes.trim() || undefined,
      setCurrent: this.saveAsCurrent,
    });
    this.saving = false;
    if (!res.success) {
      this.error = res.message ?? 'Could not save.';
      return;
    }
    this.savedMessage = res.message ?? 'Saved.';
    this.saveChecked.clear();
    await this.load();
  }

  // ---- results

  get visibleRows(): NdEvalClauseComparison[] {
    const rows = this.comparison?.clauses ?? [];
    return this.filter === 'all' ? rows : rows.filter((r) => r.change !== 'same');
  }

  isOpen(row: NdEvalClauseComparison): boolean {
    return this.open.has(row.clauseNo + ':' + (row.evalId ?? ''));
  }

  toggle(row: NdEvalClauseComparison): void {
    const k = row.clauseNo + ':' + (row.evalId ?? '');
    if (this.open.has(k)) this.open.delete(k);
    else this.open.add(k);
  }

  changeLabel(change: string): string {
    switch (change) {
      case 'same':
        return 'Same';
      case 'gaps_changed':
        return 'Gaps changed';
      case 'more_compliant':
        return 'More compliant';
      case 'less_compliant':
        return 'Less compliant';
      case 'missing_in_run':
        return 'Eval only (no clause ticked)';
      case 'new_in_run':
        return 'No eval ticked';
      case 'not_judged':
        return 'Not judged';
      default:
        return change;
    }
  }

  changeTone(change: string): string {
    switch (change) {
      case 'same':
        return 'green';
      case 'gaps_changed':
        return 'amber';
      case 'more_compliant':
      case 'less_compliant':
        return 'red';
      default:
        return 'gray';
    }
  }

  statusLabel(status: string | null): string {
    switch (status) {
      case 'compliant':
        return 'Compliant';
      case 'partial':
        return 'Partial';
      case 'non_compliant':
        return 'Non-compliant';
      case null:
      case '':
        return 'Not judged';
      default:
        return status;
    }
  }

  statusTone(status: string | null): string {
    switch (status) {
      case 'compliant':
        return 'green';
      case 'partial':
        return 'amber';
      case 'non_compliant':
        return 'red';
      default:
        return 'gray';
    }
  }

  pct(n: number | null | undefined): string {
    return n == null ? '-' : `${Math.round(n)}%`;
  }

  confidence(n: number | null): string {
    return n == null ? '-' : n.toFixed(2);
  }

  formatDate(iso: string): string {
    return new Date(iso).toLocaleDateString();
  }
}

function normalizeNo(no: string): string {
  return (no ?? '').trim().replace(/\.+$/, '');
}
