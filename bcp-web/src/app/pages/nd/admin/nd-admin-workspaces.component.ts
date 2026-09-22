import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { NdPageAlertComponent } from '../../../components/nd/nd-page-alert.component';
import { NdStatusBadgeComponent } from '../../../components/nd/nd-status-badge.component';
import {
  NdApiService,
  type NdWorkspaceListItem,
  type NdWorkspaceMember,
} from '../../../services/nd/nd-api.service';
import { NdAuthService } from '../../../services/nd/nd-auth.service';
import { matchesSearch } from '../../../../lib/nd/list-utils';

/**
 * Platform super admin only: one workspace per client (bank). Each workspace has its own users,
 * documents and analyses; its first admin is created together with the workspace and then manages
 * the rest of that workspace's users from User management.
 */
@Component({
  selector: 'app-nd-admin-workspaces',
  standalone: true,
  imports: [CommonModule, FormsModule, NdStatusBadgeComponent, NdPageAlertComponent],
  templateUrl: './nd-admin-workspaces.component.html',
  styleUrls: ['./nd-admin-users.component.scss', './nd-admin-workspaces.component.scss', '../nd-shared.scss'],
})
export class NdAdminWorkspacesComponent implements OnInit {
  private readonly api = inject(NdApiService);
  readonly auth = inject(NdAuthService);

  items: NdWorkspaceListItem[] = [];
  currentWorkspaceId: string | null = null;
  loading = true;
  creating = false;
  savingId: string | null = null;
  switchingId: string | null = null;
  message = '';
  error = '';
  searchQuery = '';

  name = '';
  slug = '';
  description = '';
  adminName = '';
  adminEmail = '';
  adminPassword = '';

  editingId: string | null = null;
  editName = '';
  editDescription = '';

  membersFor: string | null = null;
  members: NdWorkspaceMember[] = [];
  membersLoading = false;

  async ngOnInit(): Promise<void> {
    await this.auth.refreshProfile();
    await this.load();
  }

  get canManage(): boolean {
    return this.auth.canManageWorkspaces();
  }

  get visibleItems(): NdWorkspaceListItem[] {
    return this.items.filter((i) =>
      matchesSearch(this.searchQuery, [i.workspace.name, i.workspace.slug, i.workspace.description ?? '']),
    );
  }

  get membersWorkspaceName(): string {
    return this.items.find((i) => i.workspace.id === this.membersFor)?.workspace.name ?? '';
  }

  async load(): Promise<void> {
    this.loading = this.items.length === 0;
    const res = await this.api.getWorkspaces();
    if (res.success && res.data) {
      this.items = res.data;
      this.currentWorkspaceId =
        (res as { currentWorkspaceId?: string | null }).currentWorkspaceId ?? this.auth.profile()?.workspaceId ?? null;
    } else {
      this.error = res.message ?? 'Failed to load workspaces';
    }
    this.loading = false;
  }

  /** Suggest the short name from the display name until the admin edits it by hand. */
  onNameInput(value: string): void {
    const suggested = this.toSlug(this.name);
    this.name = value;
    if (!this.slug || this.slug === suggested) this.slug = this.toSlug(value);
  }

  private toSlug(v: string): string {
    return v
      .toLowerCase()
      .replace(/[^a-z0-9]+/g, '-')
      .replace(/^-+|-+$/g, '')
      .slice(0, 48);
  }

  async handleCreate(): Promise<void> {
    this.message = '';
    this.error = '';
    if (!this.name.trim()) {
      this.error = 'Workspace name is required.';
      return;
    }
    const wantsAdmin = !!(this.adminName.trim() || this.adminEmail.trim());
    if (wantsAdmin && (!this.adminName.trim() || !this.adminEmail.trim())) {
      this.error = 'Enter both the admin full name and email, or leave both empty.';
      return;
    }
    if (this.adminPassword && this.adminPassword.length < 6) {
      this.error = 'Admin password must be at least 6 characters.';
      return;
    }

    this.creating = true;
    const res = await this.api.createWorkspace({
      name: this.name.trim(),
      slug: this.slug.trim() || undefined,
      description: this.description.trim() || undefined,
      admin: wantsAdmin
        ? {
            fullName: this.adminName.trim(),
            email: this.adminEmail.trim(),
            password: this.adminPassword || undefined,
          }
        : undefined,
    });
    this.creating = false;

    if (!res.success) {
      this.error = res.message ?? 'Failed to create workspace';
      return;
    }
    const extra = res as { adminError?: string };
    if (extra.adminError) {
      this.error = res.message ?? `Workspace created, but the admin could not be created: ${extra.adminError}`;
    } else {
      this.message = wantsAdmin
        ? `Workspace "${this.name.trim()}" created with admin ${this.adminEmail.trim()}.`
        : `Workspace "${this.name.trim()}" created. Add its admin from User management after switching into it.`;
    }
    this.name = '';
    this.slug = '';
    this.description = '';
    this.adminName = '';
    this.adminEmail = '';
    this.adminPassword = '';
    await this.load();
  }

  startEdit(item: NdWorkspaceListItem): void {
    this.editingId = item.workspace.id;
    this.editName = item.workspace.name;
    this.editDescription = item.workspace.description ?? '';
  }

  cancelEdit(): void {
    this.editingId = null;
  }

  async saveEdit(): Promise<void> {
    if (!this.editingId || !this.editName.trim()) return;
    this.savingId = this.editingId;
    const res = await this.api.updateWorkspace(this.editingId, {
      name: this.editName.trim(),
      description: this.editDescription.trim(),
    });
    this.savingId = null;
    if (!res.success) {
      this.error = res.message ?? 'Failed to save workspace';
      return;
    }
    this.editingId = null;
    this.message = 'Workspace saved.';
    await this.load();
    // The top-bar name comes from the profile.
    await this.auth.refreshProfile(true);
  }

  async toggleActive(item: NdWorkspaceListItem): Promise<void> {
    const ws = item.workspace;
    if (ws.isActive) {
      const ok = window.confirm(
        `Deactivate "${ws.name}"? Its ${item.userCount} user(s) will be blocked from signing in until it is activated again. No data is deleted.`,
      );
      if (!ok) return;
    }
    this.savingId = ws.id;
    const res = await this.api.updateWorkspace(ws.id, { isActive: !ws.isActive });
    this.savingId = null;
    if (!res.success) {
      this.error = res.message ?? 'Failed to update workspace';
      return;
    }
    this.message = ws.isActive ? `"${ws.name}" deactivated.` : `"${ws.name}" activated.`;
    await this.load();
  }

  async switchTo(item: NdWorkspaceListItem): Promise<void> {
    if (item.workspace.id === this.currentWorkspaceId) return;
    this.switchingId = item.workspace.id;
    const res = await this.api.switchWorkspace(item.workspace.id);
    if (!res.success) {
      this.switchingId = null;
      this.error = res.message ?? 'Failed to switch workspace';
      return;
    }
    await this.auth.refreshProfile(true);
    // Every page caches the previous workspace's data; reload to start clean.
    window.location.assign('/nd/overview');
  }

  async toggleMembers(item: NdWorkspaceListItem): Promise<void> {
    if (this.membersFor === item.workspace.id) {
      this.membersFor = null;
      return;
    }
    this.membersFor = item.workspace.id;
    this.members = [];
    this.membersLoading = true;
    const res = await this.api.getWorkspaceMembers(item.workspace.id);
    this.membersLoading = false;
    if (res.success && res.data) this.members = res.data;
    else this.error = res.message ?? 'Failed to load users';
  }

  adminNames(item: NdWorkspaceListItem): string {
    return item.admins.map((a) => a.fullName).join(', ');
  }

  roleLabel(m: NdWorkspaceMember): string {
    if (m.role === 'super_admin') return m.isPlatformAdmin ? 'Super admin' : 'Admin';
    return m.role.charAt(0).toUpperCase() + m.role.slice(1);
  }
}
