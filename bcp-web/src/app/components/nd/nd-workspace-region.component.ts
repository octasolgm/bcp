import { ChangeDetectionStrategy, ChangeDetectorRef, Component, EventEmitter, Input, OnChanges, Output, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { NdApiService } from '../../services/nd/nd-api.service';
import type { NdJurisdictionRegion } from '../../../lib/nd/types';

/**
 * The country a business operates in, grouped by region. One choice drives two things: the workspace's date
 * convention (when it has none of its own) and which regulator rulebooks it imports on Rulebook categories.
 * A business admin sets it on Business settings; the platform admin can set any workspace's.
 */
@Component({
  selector: 'app-nd-workspace-region',
  standalone: true,
  imports: [FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (loading) {
      <p class="nd-muted">Loading regions...</p>
    } @else if (editable) {
      <form class="wr-form" (ngSubmit)="save()">
        <label class="nd-label">
          Region and country
          <select class="nd-input" name="jurisdiction" [(ngModel)]="selected">
            <option value="">Not set</option>
            @for (r of regions; track r.region) {
              <optgroup [label]="r.region">
                @for (c of r.countries; track c.code) {
                  <option [value]="c.code">{{ c.country }}</option>
                }
              </optgroup>
            }
          </select>
        </label>
        <button type="submit" class="nd-btn nd-btn-primary wr-save" [disabled]="saving">
          {{ saving ? 'Saving...' : 'Save region' }}
        </button>
      </form>
      <p class="nd-muted wr-note">
        Sets this business's date format and which rulebooks it imports on the Rulebook categories page.
        @if (currentRegulators.length) {
          <br />Rulebooks: {{ currentRegulators.join(', ') }}
        }
      </p>
    } @else {
      <dl class="wr-list">
        <div><dt>Region</dt><dd>{{ regionName || 'Not set' }}</dd></div>
        <div><dt>Country</dt><dd>{{ countryName || 'Not set' }}</dd></div>
      </dl>
      @if (currentRegulators.length) {
        <p class="nd-muted wr-note">Rulebooks: {{ currentRegulators.join(', ') }}</p>
      }
    }
    @if (error) {
      <p class="nd-error wr-msg">{{ error }}</p>
    } @else if (message) {
      <p class="nd-success wr-msg">{{ message }}</p>
    }
  `,
  styles: [
    `
      .wr-form { display: grid; grid-template-columns: repeat(auto-fit, minmax(12rem, 1fr)); gap: 0.75rem; align-items: end; }
      .wr-save { justify-self: start; }
      .wr-note { margin: 0.4rem 0 0; font-size: 0.8125rem; }
      .wr-list { display: grid; grid-template-columns: repeat(auto-fit, minmax(9rem, 1fr)); gap: 0.75rem; margin: 0; }
      .wr-list dt { font-size: 0.8125rem; color: var(--muted-foreground, var(--muted)); }
      .wr-list dd { margin: 0.1rem 0 0; font-weight: 600; }
      .wr-msg { margin: 0.4rem 0 0; font-size: 0.8125rem; }
    `,
  ],
})
export class NdWorkspaceRegionComponent implements OnChanges {
  private readonly api = inject(NdApiService);
  private readonly cdr = inject(ChangeDetectorRef);

  @Input({ required: true }) workspaceId!: string;
  /** The workspace's current jurisdiction code (e.g. "ae"), or null when not set. */
  @Input() jurisdiction: string | null = null;
  @Input() editable = false;
  /** Fires with the saved jurisdiction code so a parent list can update its row. */
  @Output() saved = new EventEmitter<{ jurisdiction: string | null; country: string | null; region: string | null }>();

  regions: NdJurisdictionRegion[] = [];
  selected = '';
  loading = true;
  saving = false;
  error = '';
  message = '';

  private loadedCatalog = false;

  get countryName(): string {
    return this.find(this.jurisdiction)?.country ?? '';
  }

  get regionName(): string {
    const code = this.jurisdiction;
    if (!code) return '';
    return this.regions.find((r) => r.countries.some((c) => c.code === code))?.region ?? '';
  }

  get currentRegulators(): string[] {
    return this.find(this.selected || this.jurisdiction)?.regulators ?? [];
  }

  private find(code: string | null) {
    if (!code) return undefined;
    for (const r of this.regions) {
      const hit = r.countries.find((c) => c.code === code);
      if (hit) return hit;
    }
    return undefined;
  }

  ngOnChanges(): void {
    this.selected = this.jurisdiction ?? '';
    if (!this.loadedCatalog) void this.load();
  }

  private async load(): Promise<void> {
    this.loading = true;
    this.cdr.markForCheck();
    const res = await this.api.getJurisdictions();
    this.loading = false;
    this.loadedCatalog = true;
    if (res.success && res.data) this.regions = res.data as NdJurisdictionRegion[];
    else this.error = res.message ?? 'Could not load the regions.';
    this.cdr.markForCheck();
  }

  async save(): Promise<void> {
    if (!this.workspaceId) return;
    this.saving = true;
    this.error = '';
    this.message = '';
    this.cdr.markForCheck();
    const res = await this.api.setWorkspaceJurisdiction(this.workspaceId, this.selected || null);
    this.saving = false;
    if (res.success && res.data) {
      this.jurisdiction = res.data.jurisdiction ?? null;
      this.message = this.jurisdiction ? `Region saved: ${res.data.region} · ${res.data.country}.` : 'Region cleared.';
      this.saved.emit({
        jurisdiction: res.data.jurisdiction ?? null,
        country: res.data.country ?? null,
        region: res.data.region ?? null,
      });
    } else {
      this.error = res.message ?? 'Could not save the region.';
    }
    this.cdr.markForCheck();
  }
}
