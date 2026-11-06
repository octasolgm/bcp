import { Component, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { NdAuthService } from '../../../services/nd/nd-auth.service';
import { NdWorkspaceModulesComponent } from '../../../components/nd/nd-workspace-modules.component';
import { NdWorkspaceClauseLimitComponent } from '../../../components/nd/nd-workspace-clause-limit.component';
import { NdWorkspaceUploadLimitsComponent } from '../../../components/nd/nd-workspace-upload-limits.component';

/**
 * Read-only view of the current workspace's modules (Actions, Embeddings), clause limit and upload limits, for
 * every role. The platform super admin changes them per business on the Business workspaces page.
 */
@Component({
  selector: 'app-nd-admin-business-settings',
  standalone: true,
  imports: [RouterLink, NdWorkspaceModulesComponent, NdWorkspaceClauseLimitComponent, NdWorkspaceUploadLimitsComponent],
  template: `
    <div class="page-header">
      <div>
        <h1>Business settings</h1>
        <p class="subtitle">Modules, clause limit and upload limits for {{ workspace()?.name || 'this business' }}.</p>
      </div>
    </div>

    <div class="nd-page-body">
      <div class="nd-page nd-page-fill">
        @if (workspace(); as ws) {
          <p class="nd-muted settings-note">
            @if (auth.isPlatformAdmin()) {
              These settings are view only here. To change them, go to
              <a routerLink="/nd/admin/workspaces">Business workspaces</a>.
            } @else {
              These settings are set by the platform administrator. Contact them to change anything here.
            }
          </p>

          <section class="nd-card settings-card">
            <header class="settings-card-header">
              <div>
                <h2>Modules</h2>
                <p class="nd-muted">Optional parts of the product for this business.</p>
              </div>
            </header>
            <app-nd-workspace-modules
              [workspaceId]="ws.id"
              [actionsEnabled]="ws.actionsEnabled"
              [embeddingsEnabled]="ws.embeddingsEnabled"
              [integrationsEnabled]="ws.integrationsEnabled"
              [editable]="false"
            />
          </section>

          <section class="nd-card settings-card">
            <header class="settings-card-header">
              <div>
                <h2>Clause limit</h2>
                <p class="nd-muted">The most clauses this business can have analysed in total. New analyses stop when it is reached.</p>
              </div>
            </header>
            <app-nd-workspace-clause-limit [workspaceId]="ws.id" [editable]="false" />
          </section>

          <section class="nd-card settings-card">
            <header class="settings-card-header">
              <div>
                <h2>Upload limits</h2>
                <p class="nd-muted">Largest single document, largest combined upload of several files, and total storage, in MB.</p>
              </div>
            </header>
            <app-nd-workspace-upload-limits [workspaceId]="ws.id" [editable]="false" />
          </section>
        } @else {
          <p class="nd-muted">Loading...</p>
        }
      </div>
    </div>
  `,
  styleUrls: ['./nd-admin-settings.component.scss', '../nd-shared.scss'],
  styles: [`.settings-card + .settings-card { margin-top: 1rem; } .settings-note { margin: 0 0 1rem; }`],
})
export class NdAdminBusinessSettingsComponent {
  readonly auth = inject(NdAuthService);
  readonly workspace = computed(() => this.auth.profile()?.workspace ?? null);
}
