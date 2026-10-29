import { Component, computed, inject } from '@angular/core';
import { NdAuthService } from '../../../services/nd/nd-auth.service';
import { NdWorkspaceModulesComponent } from '../../../components/nd/nd-workspace-modules.component';
import { NdWorkspaceUploadLimitsComponent } from '../../../components/nd/nd-workspace-upload-limits.component';

/**
 * Business settings for the signed-in admin's own workspace: its modules (Actions, Embeddings) and its
 * upload limits. Limits are set by the platform admin, so a business admin sees them read-only.
 */
@Component({
  selector: 'app-nd-admin-business-settings',
  standalone: true,
  imports: [NdWorkspaceModulesComponent, NdWorkspaceUploadLimitsComponent],
  template: `
    <div class="page-header">
      <div>
        <h1>Business settings</h1>
        <p class="subtitle">Modules and upload limits for {{ workspace()?.name || 'this business' }}.</p>
      </div>
    </div>

    <div class="nd-page-body">
      <div class="nd-page nd-page-fill">
        @if (workspace(); as ws) {
          <section class="nd-card settings-card">
            <header class="settings-card-header">
              <div>
                <h2>Modules</h2>
                <p class="nd-muted">Turn optional parts of the product on or off for this business. Nothing is deleted.</p>
              </div>
            </header>
            <app-nd-workspace-modules
              [workspaceId]="ws.id"
              [actionsEnabled]="ws.actionsEnabled"
              [embeddingsEnabled]="ws.embeddingsEnabled"
              (changed)="onModulesChanged()"
            />
          </section>

          <section class="nd-card settings-card">
            <header class="settings-card-header">
              <div>
                <h2>Upload limits</h2>
                <p class="nd-muted">
                  @if (auth.isPlatformAdmin()) {
                    Largest single document, largest combined upload of several files, and total storage, in MB.
                  } @else {
                    Set by the platform administrator. Contact them to change these limits.
                  }
                </p>
              </div>
            </header>
            <app-nd-workspace-upload-limits [workspaceId]="ws.id" [editable]="auth.isPlatformAdmin()" />
          </section>
        } @else {
          <p class="nd-muted">Loading...</p>
        }
      </div>
    </div>
  `,
  styleUrls: ['./nd-admin-settings.component.scss', '../nd-shared.scss'],
  styles: [`.settings-card + .settings-card { margin-top: 1rem; }`],
})
export class NdAdminBusinessSettingsComponent {
  readonly auth = inject(NdAuthService);
  readonly workspace = computed(() => this.auth.profile()?.workspace ?? null);

  /** Reports read the modules from the signed-in profile, so reload it after a change. */
  onModulesChanged(): void {
    void this.auth.refreshProfile(true);
  }
}
