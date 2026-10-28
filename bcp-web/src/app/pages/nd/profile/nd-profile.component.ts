import { ChangeDetectionStrategy, ChangeDetectorRef, Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { NdApiService } from '../../../services/nd/nd-api.service';
import { NdAuthService } from '../../../services/nd/nd-auth.service';
import { NdPageAlertComponent } from '../../../components/nd/nd-page-alert.component';

const ROLE_LABELS: Record<string, string> = {
  super_admin: 'Super admin',
  maker: 'Maker',
  checker: 'Checker',
  reviewer: 'Reviewer',
};

/**
 * The signed-in user's own profile. Name and password are self-service; role and department are set by
 * an admin in User management, because department drives who receives assigned actions.
 */
@Component({
  selector: 'app-nd-profile',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink, NdPageAlertComponent],
  templateUrl: './nd-profile.component.html',
  styleUrls: ['./nd-profile.component.scss', '../nd-shared.scss'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class NdProfileComponent implements OnInit {
  readonly auth = inject(NdAuthService);
  private readonly api = inject(NdApiService);
  private readonly cdr = inject(ChangeDetectorRef);

  fullName = '';
  saving = false;
  message = '';
  error = '';

  newPassword = '';
  confirmPassword = '';
  changingPassword = false;
  showPassword = false;
  passwordMessage = '';
  passwordError = '';

  inbox = { pending: 0, resolved: 0, overdue: 0, total: 0 };

  get profile() {
    return this.auth.profile();
  }

  get roleLabel(): string {
    const role = this.profile?.role ?? '';
    return ROLE_LABELS[role] ?? role;
  }

  get displayName(): string {
    const n = (this.fullName || this.profile?.fullName || '').trim();
    return n || 'Your profile';
  }

  get initials(): string {
    const parts = this.displayName.split(/\s+/).filter(Boolean);
    if (!parts.length) return '?';
    if (parts.length === 1) return parts[0].slice(0, 2).toUpperCase();
    return (parts[0][0] + parts[parts.length - 1][0]).toUpperCase();
  }

  get workspaceLabel(): string | null {
    const w = this.profile?.workspace;
    if (!w?.name) return null;
    return w.name;
  }

  get showAiCreditsLink(): boolean {
    return !this.auth.isDemoViewer();
  }

  get showBillingLinks(): boolean {
    return this.auth.canManageWorkspaces() || this.showAiCreditsLink;
  }

  async ngOnInit(): Promise<void> {
    await this.auth.refreshProfile();
    this.fullName = this.profile?.fullName ?? '';
    this.cdr.markForCheck();

    const res = await this.api.getActionPlanInbox('all');
    if (res.success && res.data) this.inbox = res.data.counts;
    this.cdr.markForCheck();
  }

  async save(): Promise<void> {
    const name = this.fullName.trim();
    if (!name) {
      this.error = 'Your name cannot be empty.';
      this.cdr.markForCheck();
      return;
    }

    this.saving = true;
    this.message = '';
    this.error = '';
    const res = await this.api.upsertProfile({ fullName: name });
    this.saving = false;

    if (!res.success) {
      this.error = res.message ?? 'Could not save your profile.';
      this.cdr.markForCheck();
      return;
    }

    await this.auth.refreshProfile(true);
    this.message = 'Profile updated.';
    this.cdr.markForCheck();
  }

  async changePassword(): Promise<void> {
    this.passwordMessage = '';
    this.passwordError = '';

    const pwd = this.newPassword;
    const confirm = this.confirmPassword;
    if (pwd.length < 6) {
      this.passwordError = 'Password must be at least 6 characters.';
      this.cdr.markForCheck();
      return;
    }
    if (pwd !== confirm) {
      this.passwordError = 'Passwords do not match.';
      this.cdr.markForCheck();
      return;
    }

    this.changingPassword = true;
    const err = await this.auth.changePassword(pwd);
    this.changingPassword = false;

    if (err) {
      this.passwordError = err;
      this.cdr.markForCheck();
      return;
    }

    this.newPassword = '';
    this.confirmPassword = '';
    this.passwordMessage = 'Password updated.';
    this.cdr.markForCheck();
  }
}
