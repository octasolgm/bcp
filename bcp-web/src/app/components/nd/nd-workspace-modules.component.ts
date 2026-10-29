import { ChangeDetectionStrategy, ChangeDetectorRef, Component, EventEmitter, Input, Output, inject } from '@angular/core';
import { NdApiService } from '../../services/nd/nd-api.service';

/**
 * On/off switches for a workspace's optional modules. Actions off only hides corrective actions in that
 * business's reports; Embeddings off means finalize creates no finalized (embedded) document copies.
 * Embeddings depend on Actions, so turning Actions off turns Embeddings off too.
 */
@Component({
  selector: 'app-nd-workspace-modules',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="mod-list">
      <label class="mod-row">
        <span class="mod-text">
          <strong>Actions</strong>
          <span class="mod-hint">Show corrective actions in analysis reports. Off hides them; gaps and saved actions are kept.</span>
        </span>
        <input
          type="checkbox"
          role="switch"
          class="mod-switch"
          [checked]="actionsEnabled !== false"
          [disabled]="saving"
          (change)="set({ actionsEnabled: $any($event.target).checked })"
        />
      </label>
      <label class="mod-row" [class.mod-off]="actionsEnabled === false">
        <span class="mod-text">
          <strong>Embeddings</strong>
          <span class="mod-hint">
            On finalize, write the resolved actions into new versions of the internal documents.
            @if (actionsEnabled === false) { Needs Actions on. }
          </span>
        </span>
        <input
          type="checkbox"
          role="switch"
          class="mod-switch"
          [checked]="actionsEnabled !== false && embeddingsEnabled !== false"
          [disabled]="saving || actionsEnabled === false"
          (change)="set({ embeddingsEnabled: $any($event.target).checked })"
        />
      </label>
      @if (error) {
        <p class="nd-error mod-msg">{{ error }}</p>
      } @else if (saving) {
        <p class="nd-muted mod-msg">Saving...</p>
      }
    </div>
  `,
  styles: [
    `
      .mod-list { display: flex; flex-direction: column; gap: 0.25rem; }
      .mod-row {
        display: flex; align-items: center; justify-content: space-between; gap: 1rem;
        padding: 0.6rem 0; border-bottom: 1px solid var(--border); cursor: pointer;
      }
      .mod-row:last-of-type { border-bottom: 0; }
      .mod-off { opacity: 0.6; }
      .mod-text { display: flex; flex-direction: column; gap: 0.15rem; }
      .mod-hint { font-size: 0.8125rem; color: var(--muted-foreground, var(--muted)); }
      .mod-msg { margin: 0.25rem 0 0; font-size: 0.8125rem; }
      .mod-switch {
        appearance: none; flex: none; width: 2.4rem; height: 1.35rem; border-radius: 999px;
        background: var(--border); position: relative; cursor: pointer; transition: background 0.15s ease;
      }
      .mod-switch::after {
        content: ''; position: absolute; top: 0.15rem; left: 0.15rem; width: 1.05rem; height: 1.05rem;
        border-radius: 50%; background: #fff; transition: transform 0.15s ease;
        box-shadow: 0 1px 2px rgb(0 0 0 / 0.25);
      }
      .mod-switch:checked { background: var(--accent); }
      .mod-switch:checked::after { transform: translateX(1.05rem); }
      .mod-switch:disabled { cursor: not-allowed; }
      .mod-switch:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
    `,
  ],
})
export class NdWorkspaceModulesComponent {
  private readonly api = inject(NdApiService);
  private readonly cdr = inject(ChangeDetectorRef);

  @Input({ required: true }) workspaceId!: string;
  @Input() actionsEnabled: boolean | undefined = true;
  @Input() embeddingsEnabled: boolean | undefined = true;
  @Output() readonly changed = new EventEmitter<{ actionsEnabled: boolean; embeddingsEnabled: boolean }>();

  saving = false;
  error = '';

  async set(modules: { actionsEnabled?: boolean; embeddingsEnabled?: boolean }): Promise<void> {
    this.saving = true;
    this.error = '';
    this.cdr.markForCheck();
    const res = await this.api.setWorkspaceModules(this.workspaceId, modules);
    this.saving = false;
    if (res.success && res.data) {
      this.actionsEnabled = res.data.actionsEnabled;
      this.embeddingsEnabled = res.data.embeddingsEnabled;
      this.changed.emit(res.data);
    } else {
      this.error = res.message ?? 'Could not save.';
    }
    this.cdr.markForCheck();
  }
}
