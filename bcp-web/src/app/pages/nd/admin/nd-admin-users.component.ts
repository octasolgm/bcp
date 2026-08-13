import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { NdPageAlertComponent } from '../../../components/nd/nd-page-alert.component';
import { NdStatusBadgeComponent } from '../../../components/nd/nd-status-badge.component';
import { NdApiService } from '../../../services/nd/nd-api.service';
import { NdAuthService } from '../../../services/nd/nd-auth.service';
import {
  compareText,
  hasListFilters,
  matchesSearch,
  nextSortState,
  sortIndicator,
  type SortDir,
} from '../../../../lib/nd/list-utils';

type AdminUser = {
  id: string;
  fullName: string;
  email?: string | null;
  role: string;
  isActive: boolean;
  accountStatus: 'active' | 'deactivated' | 'pending_invitation' | string;
  createdAt: string;
};

type UserSortColumn = 'name' | 'email' | 'role' | 'status';

@Component({
  selector: 'app-nd-admin-users',
  standalone: true,
  imports: [CommonModule, FormsModule, NdStatusBadgeComponent, NdPageAlertComponent],
  templateUrl: './nd-admin-users.component.html',
  styleUrls: ['./nd-admin-users.component.scss', '../nd-shared.scss'],
})
export class NdAdminUsersComponent implements OnInit {
  private readonly api = inject(NdApiService);
  readonly auth = inject(NdAuthService);

  users: AdminUser[] = [];
  inviteName = '';
  inviteEmail = '';
  inviteRole = 'maker';
  invitePassword = '';
  loading = true;
  inviting = false;
  deletingId: string | null = null;
  savingUserId: string | null = null;
  resettingId: string | null = null;
  resetPasswordValue = '';
  resetPasswordUserId: string | null = null;
  message = '';
  error = '';
  searchQuery = '';
  roleFilter = '';
  statusFilter = '';
  sortColumn: UserSortColumn = 'name';
  sortDir: SortDir = 'asc';

  async ngOnInit(): Promise<void> {
    await this.auth.refreshProfile();
    await this.load();
  }

  get isSuperAdmin(): boolean {
    return this.auth.getRole() === 'super_admin';
  }

  get currentUserId(): string | undefined {
    return this.auth.profile()?.id;
  }

  get resetPasswordUser(): AdminUser | undefined {
    if (!this.resetPasswordUserId) return undefined;
    return this.users.find((u) => u.id === this.resetPasswordUserId);
  }

  async load(silent = false): Promise<void> {
    if (!silent) this.loading = true;
    this.error = '';
    const usersRes = await this.api.getUsers();
    if (usersRes.success && usersRes.data) {
      this.users = usersRes.data as AdminUser[];
    } else if (!silent || this.users.length === 0) {
      this.error = usersRes.message ?? 'Failed to load users';
    }
    this.loading = false;
  }

  get visibleUsers(): AdminUser[] {
    let list = this.users.filter((u) => {
      if (!matchesSearch(this.searchQuery, [u.fullName, u.email])) return false;
      if (this.roleFilter && u.role !== this.roleFilter) return false;
      const status = u.accountStatus || (u.isActive ? 'active' : 'deactivated');
      if (this.statusFilter && status !== this.statusFilter) return false;
      return true;
    });

    return [...list].sort((a, b) => {
      switch (this.sortColumn) {
        case 'email':
          return compareText(a.email ?? '', b.email ?? '', this.sortDir);
        case 'role':
          return compareText(a.role, b.role, this.sortDir);
        case 'status': {
          const sa = a.accountStatus || (a.isActive ? 'active' : 'deactivated');
          const sb = b.accountStatus || (b.isActive ? 'active' : 'deactivated');
          return compareText(sa, sb, this.sortDir);
        }
        case 'name':
        default:
          return compareText(a.fullName, b.fullName, this.sortDir);
      }
    });
  }

  get hasActiveFilters(): boolean {
    return hasListFilters(this.searchQuery, this.roleFilter, this.statusFilter);
  }

  toggleSort(column: UserSortColumn): void {
    const next = nextSortState(this.sortColumn, column, this.sortDir, 'name');
    this.sortColumn = next.column;
    this.sortDir = next.dir;
  }

  sortMark(column: UserSortColumn): string {
    return sortIndicator(this.sortColumn, column, this.sortDir);
  }

  clearFilters(): void {
    this.searchQuery = '';
    this.roleFilter = '';
    this.statusFilter = '';
  }

  async handleInvite(): Promise<void> {
    this.inviting = true;
    this.error = '';
    this.message = '';
    const password = this.invitePassword.trim();
    if (password && password.length < 6) {
      this.error = 'Password must be at least 6 characters.';
      this.inviting = false;
      return;
    }
    const res = await this.api.inviteUser({
      fullName: this.inviteName.trim(),
      email: this.inviteEmail.trim(),
      role: this.inviteRole,
      password: password || undefined,
    });
    if (res.success) {
      this.message = res.message ?? 'User created';
      this.inviteName = '';
      this.inviteEmail = '';
      this.invitePassword = '';
      await this.load(true);
    } else {
      this.error = this.formatApiError(res.message ?? 'Create user failed');
    }
    this.inviting = false;
  }

  private formatApiError(message: string): string {
    const trimmed = message.trim();
    if (!trimmed.startsWith('{')) return trimmed;
    try {
      const parsed = JSON.parse(trimmed) as { msg?: string; message?: string };
      return parsed.msg ?? parsed.message ?? trimmed;
    } catch {
      return trimmed;
    }
  }

  async handleRoleChange(userId: string, role: string): Promise<void> {
    const idx = this.users.findIndex((u) => u.id === userId);
    if (idx < 0) return;
    const prevRole = this.users[idx].role;
    if (prevRole === role) return;

    this.savingUserId = userId;
    this.error = '';
    this.users[idx] = { ...this.users[idx], role };

    const res = await this.api.updateUser(userId, { role });
    if (!res.success) {
      this.users[idx] = { ...this.users[idx], role: prevRole };
      this.error = res.message ?? 'Failed to update role';
    }
    this.savingUserId = null;
  }

  async toggleActive(user: AdminUser): Promise<void> {
    this.savingUserId = user.id;
    this.error = '';
    const wasActive = user.isActive;
    user.isActive = !wasActive;
    user.accountStatus = user.isActive ? 'active' : 'deactivated';

    const res = wasActive
      ? await this.api.deactivateUser(user.id)
      : await this.api.activateUser(user.id);
    if (!res.success) {
      user.isActive = wasActive;
      user.accountStatus = wasActive ? 'active' : 'deactivated';
      this.error = res.message ?? 'Failed to update status';
    }
    this.savingUserId = null;
  }

  async handleDelete(user: AdminUser): Promise<void> {
    if (user.id === this.currentUserId) {
      this.error = 'You cannot delete your own account';
      return;
    }
    const label = user.email ? `${user.fullName} (${user.email})` : user.fullName;
    if (!confirm(`Delete user "${label}" permanently?\n\nThis removes their login and profile.`)) {
      return;
    }
    this.deletingId = user.id;
    this.error = '';
    const res = await this.api.deleteUser(user.id);
    if (res.success) {
      this.message = 'User deleted';
      this.users = this.users.filter((u) => u.id !== user.id);
    } else {
      this.error = res.message ?? 'Delete failed';
    }
    this.deletingId = null;
  }

  openResetPassword(user: AdminUser): void {
    this.resetPasswordUserId = user.id;
    this.resetPasswordValue = '';
    this.error = '';
    this.message = '';
  }

  cancelResetPassword(): void {
    this.resetPasswordUserId = null;
    this.resetPasswordValue = '';
  }

  async handleSetPassword(): Promise<void> {
    if (!this.resetPasswordUserId) return;
    const password = this.resetPasswordValue.trim();
    if (password.length < 6) {
      this.error = 'Password must be at least 6 characters.';
      return;
    }
    this.resettingId = this.resetPasswordUserId;
    this.error = '';
    const res = await this.api.setUserPassword(this.resetPasswordUserId, password);
    if (res.success) {
      this.message = res.message ?? 'Password updated — user can sign in without email verification.';
      this.cancelResetPassword();
      await this.load(true);
    } else {
      this.error = this.formatApiError(res.message ?? 'Failed to set password');
    }
    this.resettingId = null;
  }
}
