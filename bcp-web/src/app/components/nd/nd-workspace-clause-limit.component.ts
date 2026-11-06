import { ChangeDetectionStrategy, ChangeDetectorRef, Component, Input, OnChanges, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { NdApiService } from '../../services/nd/nd-api.service';

/**
 * The current workspace's clause limit: the most clauses it can have analysed in total, with how many it has
 * used. Editable only by the platform admin (`editable`); a business admin sees it read-only on Business settings.
 */
@Component({
  selector: 'app-nd-workspace-clause-limit',
  standalone: true,
  imports: [FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (loading) {
      <p class="nd-muted">Loading limit...</p>
    } @else {
      @if (editable) {
        <form class="cl-form" (ngSubmit)="save()">
          <label class="nd-label">
            Clause limit
            <input class="nd-input" type="number" min="0" step="1" name="clauseLimit" [(ngModel)]="limitInput" placeholder="No limit" />
          </label>
          <button type="submit" class="nd-btn nd-btn-primary cl-save" [disabled]="saving || !!validationError">
            {{ saving ? 'Saving...' : 'Save limit' }}
          </button>
        </form>
        <p class="nd-muted cl-note">Leave the box empty for no limit.</p>
        @if (validationError) {
          <p class="nd-error cl-msg">{{ validationError }}</p>
        }
      } @else {
        <dl class="cl-list">
          <div><dt>Clause limit</dt><dd>{{ limit ?? 'No limit' }}</dd></div>
          <div><dt>Clauses per analysis</dt><dd>{{ perAnalysis ?? 'No limit' }}</dd></div>
        </dl>
      }
      <p class="cl-used">
        Clauses used: <strong>{{ used }}</strong>
        @if (limit != null) {
          of {{ limit }} ({{ remaining }} left)
        }
      </p>
    }
    @if (error) {
      <p class="nd-error cl-msg">{{ error }}</p>
    } @else if (message) {
      <p class="nd-success cl-msg">{{ message }}</p>
    }
  `,
  styles: [
    `
      .cl-form { display: grid; grid-template-columns: repeat(auto-fit, minmax(9rem, 1fr)); gap: 0.75rem; align-items: end; }
      .cl-save { justify-self: start; }
      .cl-note { margin: 0.4rem 0 0; font-size: 0.8125rem; }
      .cl-list { display: grid; grid-template-columns: repeat(auto-fit, minmax(9rem, 1fr)); gap: 0.75rem; margin: 0; }
      .cl-list dt { font-size: 0.8125rem; color: var(--muted-foreground, var(--muted)); }
      .cl-list dd { margin: 0.1rem 0 0; font-weight: 600; }
      .cl-used { margin: 0.75rem 0 0; font-size: 0.875rem; }
      .cl-msg { margin: 0.4rem 0 0; font-size: 0.8125rem; }
    `,
  ],
})
export class NdWorkspaceClauseLimitComponent implements OnChanges {
  private readonly api = inject(NdApiService);
  private readonly cdr = inject(ChangeDetectorRef);

  @Input({ required: true }) workspaceId!: string;
  @Input() editable = false;

  limit: number | null = null;
  used = 0;
  remaining: number | null = null;
  perAnalysis: number | null = null;
  limitInput: number | null = null;
  loading = true;
  saving = false;
  error = '';
  message = '';

  /** Why the value in the box cannot be saved, or '' when it can. */
  get validationError(): string {
    const value = this.limitInput;
    return value != null && (!Number.isInteger(value) || value < 0) ? 'The clause limit must be a whole number of 0 or more.' : '';
  }

  ngOnChanges(): void {
    void this.load();
  }

  private apply(limit: number | null | undefined, used: number | undefined, remaining: number | null | undefined): void {
    this.limit = limit ?? null;
    this.used = used ?? 0;
    this.remaining = remaining ?? null;
    this.limitInput = this.limit;
  }

  private async load(): Promise<void> {
    if (!this.workspaceId) return;
    this.loading = true;
    this.cdr.markForCheck();
    const res = await this.api.getMyAiCredits();
    this.loading = false;
    if (res.success && res.data) {
      this.apply(res.data.clauseLimit, res.data.clausesUsed, res.data.clausesRemaining);
      this.perAnalysis = res.data.maxClausesPerAnalysis ?? null;
    } else this.error = res.message ?? 'Could not load the clause limit.';
    this.cdr.markForCheck();
  }

  async save(): Promise<void> {
    if (this.validationError) return;
    this.saving = true;
    this.error = '';
    this.message = '';
    this.cdr.markForCheck();
    const res = await this.api.setWorkspaceClauseLimit(this.workspaceId, this.limitInput);
    this.saving = false;
    if (res.success && res.data) {
      this.apply(res.data.clauseLimit, res.data.clausesUsed, res.data.clausesRemaining);
      this.message = 'Limit saved.';
    } else {
      this.error = res.message ?? 'Could not save the limit.';
    }
    this.cdr.markForCheck();
  }
}
