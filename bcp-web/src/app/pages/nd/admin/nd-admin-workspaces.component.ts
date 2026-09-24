import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { NdPageAlertComponent } from '../../../components/nd/nd-page-alert.component';
import { NdStatusBadgeComponent } from '../../../components/nd/nd-status-badge.component';
import {
  NdApiService,
  type NdWorkspaceCredits,
  type NdWorkspaceListItem,
  type NdWorkspaceMember,
} from '../../../services/nd/nd-api.service';
import { RouterLink } from '@angular/router';
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
  imports: [CommonModule, FormsModule, RouterLink, NdStatusBadgeComponent, NdPageAlertComponent],
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

  /** Credit balances per workspace id, filled as the list loads. */
  credits = new Map<string, NdWorkspaceCredits>();
  creditsFor: string | null = null;
  creditsLoading = false;
  topUpAmount: number | null = null;
  topUpNote = '';
  toppingUp = false;

  /** AI credit price form (platform admin). */
  priceUsdPerCredit = 0.01;
  priceMarkup = 1;
  priceMinMargin = 20;
  pricingSaving = false;

  async ngOnInit(): Promise<void> {
    await this.auth.refreshProfile();
    await this.load();
    await this.loadPricing();
  }

  private async loadPricing(): Promise<void> {
    if (!this.canManage) return;
    const res = await this.api.getAiPricing();
    if (res.success && res.data) {
      this.priceUsdPerCredit = res.data.usdPerCredit;
      this.priceMarkup = res.data.markup;
      this.priceMinMargin = res.data.minMarginPct;
    }
  }

  get previewCreditsPerDollar(): number {
    return this.priceUsdPerCredit > 0 ? 1 / this.priceUsdPerCredit : 0;
  }

  /** What a client is charged for an AI call that costs us $1.00. */
  get previewBilled(): number {
    return this.priceMarkup > 0 ? this.priceMarkup : 1;
  }

  get previewMarginPct(): number {
    return this.previewBilled > 0 ? ((this.previewBilled - 1) / this.previewBilled) * 100 : 0;
  }

  async savePricing(): Promise<void> {
    this.pricingSaving = true;
    const res = await this.api.setAiPricing({
      usdPerCredit: Number(this.priceUsdPerCredit),
      markup: Number(this.priceMarkup),
      minMarginPct: Number(this.priceMinMargin),
    });
    this.pricingSaving = false;
    if (!res.success) {
      this.error = res.message ?? 'Could not save the credit price';
      return;
    }
    this.message = res.message ?? 'Credit price saved.';
    await this.loadCredits();
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
      await this.loadCredits();
    } else {
      this.error = res.message ?? 'Failed to load workspaces';
    }
    this.loading = false;
  }

  /** Balances for the whole list, so the table can show them without a click. */
  private async loadCredits(): Promise<void> {
    const results = await Promise.all(
      this.items.map(async (item) => {
        const res = await this.api.getWorkspaceAiCredits(item.workspace.id);
        return [item.workspace.id, res.success ? res.data ?? null : null] as const;
      }),
    );
    for (const [id, data] of results) {
      if (data) this.credits.set(id, data);
    }
  }

  creditsOf(item: NdWorkspaceListItem): NdWorkspaceCredits | null {
    return this.credits.get(item.workspace.id) ?? null;
  }

  creditLabel(item: NdWorkspaceListItem): string {
    const c = this.creditsOf(item);
    if (!c) return '-';
    if (c.summary.unlimited) return 'No limit';
    return `${c.summary.balance.toFixed(2)} left`;
  }

  creditState(item: NdWorkspaceListItem): 'ok' | 'low' | 'empty' | 'unlimited' {
    const c = this.creditsOf(item);
    if (!c || c.summary.unlimited) return 'unlimited';
    if (c.summary.isExhausted) return 'empty';
    if (c.summary.isLow) return 'low';
    return 'ok';
  }

  async toggleCredits(item: NdWorkspaceListItem): Promise<void> {
    if (this.creditsFor === item.workspace.id) {
      this.creditsFor = null;
      return;
    }
    this.creditsFor = item.workspace.id;
    this.topUpAmount = null;
    this.topUpNote = '';
    this.creditsLoading = true;
    const res = await this.api.getWorkspaceAiCredits(item.workspace.id);
    this.creditsLoading = false;
    if (res.success && res.data) this.credits.set(item.workspace.id, res.data);
    else this.error = res.message ?? 'Could not load credits';
  }

  async handleTopUp(item: NdWorkspaceListItem): Promise<void> {
    const amount = Number(this.topUpAmount);
    if (!amount) {
      this.error = 'Enter how many credits to add.';
      return;
    }
    this.toppingUp = true;
    const res = await this.api.topUpWorkspaceAiCredits(item.workspace.id, {
      credits: amount,
      note: this.topUpNote.trim() || undefined,
    });
    this.toppingUp = false;
    if (!res.success) {
      this.error = res.message ?? 'Could not add credits';
      return;
    }
    this.message = res.message ?? 'Credits updated.';
    this.topUpAmount = null;
    this.topUpNote = '';
    const refreshed = await this.api.getWorkspaceAiCredits(item.workspace.id);
    if (refreshed.success && refreshed.data) this.credits.set(item.workspace.id, refreshed.data);
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
