import { ChangeDetectionStrategy, ChangeDetectorRef, Component, Input, OnChanges, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { NdApiService } from '../../services/nd/nd-api.service';
import { formatMb, type NdUploadLimits } from '../../../lib/nd/upload-limits';

/**
 * A workspace's upload limits in MB: per document, per upload (several files picked together) and total
 * storage, with the storage used so far. Editable only by the platform admin (`editable`); a business admin
 * sees them read-only on Business settings.
 */
@Component({
  selector: 'app-nd-workspace-upload-limits',
  standalone: true,
  imports: [FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (loading) {
      <p class="nd-muted">Loading limits...</p>
    } @else if (editable) {
      <form class="ul-form" (ngSubmit)="save()">
        <label class="nd-label">
          Per document (MB)
          <input class="nd-input" type="number" min="1" max="50" name="doc" [(ngModel)]="docMb" placeholder="No limit" />
        </label>
        <label class="nd-label">
          Per upload (MB)
          <input class="nd-input" type="number" min="1" name="upload" [(ngModel)]="uploadMb" placeholder="No limit" />
        </label>
        <label class="nd-label">
          Total storage (MB)
          <input class="nd-input" type="number" min="1" name="storage" [(ngModel)]="storageMb" placeholder="No limit" />
        </label>
        <button type="submit" class="nd-btn nd-btn-primary ul-save" [disabled]="saving">
          {{ saving ? 'Saving...' : 'Save limits' }}
        </button>
      </form>
      <p class="nd-muted ul-note">Leave a box empty for no limit. A single document can never be above 50 MB.</p>
    } @else {
      <dl class="ul-list">
        <div><dt>Per document</dt><dd>{{ show(limits?.maxDocumentMb) }}</dd></div>
        <div><dt>Per upload</dt><dd>{{ show(limits?.maxUploadMb) }}</dd></div>
        <div><dt>Total storage</dt><dd>{{ show(limits?.maxStorageMb) }}</dd></div>
      </dl>
    }
    @if (limits && !loading) {
      <p class="ul-used">
        Storage used: <strong>{{ used }}</strong>
        @if (limits.maxStorageMb) {
          of {{ limits.maxStorageMb }} MB
          <span class="ul-bar" aria-hidden="true"><span [style.width.%]="usedPct"></span></span>
        }
      </p>
    }
    @if (error) {
      <p class="nd-error ul-msg">{{ error }}</p>
    } @else if (message) {
      <p class="nd-success ul-msg">{{ message }}</p>
    }
  `,
  styles: [
    `
      .ul-form { display: grid; grid-template-columns: repeat(auto-fit, minmax(9rem, 1fr)); gap: 0.75rem; align-items: end; }
      .ul-save { justify-self: start; }
      .ul-note { margin: 0.4rem 0 0; font-size: 0.8125rem; }
      .ul-list { display: grid; grid-template-columns: repeat(auto-fit, minmax(9rem, 1fr)); gap: 0.75rem; margin: 0; }
      .ul-list dt { font-size: 0.8125rem; color: var(--muted-foreground, var(--muted)); }
      .ul-list dd { margin: 0.1rem 0 0; font-weight: 600; }
      .ul-used { margin: 0.75rem 0 0; font-size: 0.875rem; display: flex; align-items: center; gap: 0.4rem; flex-wrap: wrap; }
      .ul-bar { display: inline-block; width: 8rem; height: 0.4rem; border-radius: 999px; background: var(--border); overflow: hidden; }
      .ul-bar span { display: block; height: 100%; background: var(--accent); }
      .ul-msg { margin: 0.4rem 0 0; font-size: 0.8125rem; }
    `,
  ],
})
export class NdWorkspaceUploadLimitsComponent implements OnChanges {
  private readonly api = inject(NdApiService);
  private readonly cdr = inject(ChangeDetectorRef);

  @Input({ required: true }) workspaceId!: string;
  @Input() editable = false;

  limits: NdUploadLimits | null = null;
  docMb: number | null = null;
  uploadMb: number | null = null;
  storageMb: number | null = null;
  loading = true;
  saving = false;
  error = '';
  message = '';

  get used(): string {
    return formatMb(this.limits?.usedStorageBytes ?? 0);
  }

  get usedPct(): number {
    const max = this.limits?.maxStorageMb;
    if (!max) return 0;
    return Math.min(100, ((this.limits!.usedStorageBytes / (1024 * 1024)) / max) * 100);
  }

  show(mb: number | null | undefined): string {
    return mb ? `${mb} MB` : 'No limit';
  }

  ngOnChanges(): void {
    void this.load();
  }

  private apply(limits: NdUploadLimits): void {
    this.limits = limits;
    this.docMb = limits.maxDocumentMb;
    this.uploadMb = limits.maxUploadMb;
    this.storageMb = limits.maxStorageMb;
  }

  private async load(): Promise<void> {
    if (!this.workspaceId) return;
    this.loading = true;
    this.cdr.markForCheck();
    const res = await this.api.getWorkspaceUploadLimits(this.workspaceId);
    this.loading = false;
    if (res.success && res.data) this.apply(res.data);
    else this.error = res.message ?? 'Could not load the upload limits.';
    this.cdr.markForCheck();
  }

  async save(): Promise<void> {
    this.saving = true;
    this.error = '';
    this.message = '';
    this.cdr.markForCheck();
    const res = await this.api.setWorkspaceUploadLimits(this.workspaceId, {
      maxDocumentMb: this.docMb || null,
      maxUploadMb: this.uploadMb || null,
      maxStorageMb: this.storageMb || null,
    });
    this.saving = false;
    if (res.success && res.data) {
      this.apply(res.data);
      this.message = 'Limits saved.';
    } else {
      this.error = res.message ?? 'Could not save the limits.';
    }
    this.cdr.markForCheck();
  }
}
