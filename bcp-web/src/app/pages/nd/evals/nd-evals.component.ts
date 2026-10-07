import { Component, HostListener, OnInit, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { NdApiService, NdClauseEvalDetail, NdClauseEvalSummary } from '../../../services/nd/nd-api.service';

type ClauseGroup = {
  clauseKey: string;
  clauseNo: string;
  clauseTitle: string;
  regulationName: string | null;
  versions: NdClauseEvalSummary[];
};

/**
 * Clause evals: saved reference results per clause (3.5 v1, v2, ...), one current version per clause.
 * Analysis reports compare their clauses against the current versions. Platform super admins only.
 */
@Component({
  selector: 'app-nd-evals',
  standalone: true,
  imports: [RouterLink],
  templateUrl: './nd-evals.component.html',
  styleUrl: './nd-evals.component.scss',
})
export class NdEvalsComponent implements OnInit {
  private readonly ndApi = inject(NdApiService);

  groups: ClauseGroup[] = [];
  loading = false;
  error = '';
  busyId = '';
  private readonly expanded = new Set<string>();

  detail: NdClauseEvalDetail | null = null;
  detailLoading = false;

  async ngOnInit(): Promise<void> {
    await this.load();
  }

  async load(): Promise<void> {
    this.loading = true;
    this.error = '';
    const res = await this.ndApi.listEvals();
    this.loading = false;
    if (!res.success) {
      this.error = res.message ?? 'Could not load evals.';
      return;
    }
    const byKey = new Map<string, ClauseGroup>();
    for (const e of res.data ?? []) {
      let g = byKey.get(e.clauseKey);
      if (!g) {
        g = { clauseKey: e.clauseKey, clauseNo: e.clauseNo, clauseTitle: e.clauseTitle, regulationName: e.regulationName, versions: [] };
        byKey.set(e.clauseKey, g);
      }
      g.versions.push(e);
    }
    this.groups = [...byKey.values()];
  }

  current(g: ClauseGroup): NdClauseEvalSummary | undefined {
    return g.versions.find((v) => v.isCurrent);
  }

  isExpanded(g: ClauseGroup): boolean {
    return this.expanded.has(g.clauseKey);
  }

  toggle(g: ClauseGroup): void {
    if (this.expanded.has(g.clauseKey)) this.expanded.delete(g.clauseKey);
    else this.expanded.add(g.clauseKey);
  }

  async setCurrent(e: NdClauseEvalSummary): Promise<void> {
    this.busyId = e.id;
    const res = await this.ndApi.setCurrentEval(e.id);
    this.busyId = '';
    if (!res.success) {
      this.error = res.message ?? 'Could not set the current version.';
      return;
    }
    await this.load();
  }

  async remove(e: NdClauseEvalSummary): Promise<void> {
    const extra = e.isCurrent ? ' The newest remaining version becomes current.' : '';
    if (!confirm(`Delete eval ${e.clauseNo} v${e.versionNumber}?${extra} The analysis it came from is not affected.`)) return;
    this.busyId = e.id;
    const res = await this.ndApi.deleteEval(e.id);
    this.busyId = '';
    if (!res.success) {
      this.error = res.message ?? 'Could not delete the eval.';
      return;
    }
    if (this.detail?.eval.id === e.id) this.detail = null;
    await this.load();
  }

  async view(e: NdClauseEvalSummary): Promise<void> {
    this.detailLoading = true;
    this.detail = null;
    const res = await this.ndApi.getEval(e.id);
    this.detailLoading = false;
    if (!res.success || !res.data) {
      this.error = res.message ?? 'Could not load the eval.';
      return;
    }
    this.detail = res.data;
  }

  closeDetail(): void {
    this.detail = null;
    this.detailLoading = false;
  }

  @HostListener('document:keydown.escape')
  onEscape(): void {
    if (this.detail || this.detailLoading) this.closeDetail();
  }

  promptName(key: string): string {
    if (key.endsWith('_system')) return 'System prompt';
    if (key.endsWith('_user_context')) return 'User block 1 (policy context)';
    if (key.endsWith('_user_query')) return 'User block 2 (clause)';
    return key;
  }

  statusLabel(status: string): string {
    switch (status) {
      case 'compliant':
        return 'Compliant';
      case 'partial':
        return 'Partial';
      case 'non_compliant':
        return 'Non-compliant';
      case '':
        return 'Not judged';
      default:
        return status;
    }
  }

  statusTone(status: string): string {
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

  formatDate(iso: string): string {
    return new Date(iso).toLocaleString();
  }
}
