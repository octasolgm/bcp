import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { NdAuthService } from '../../../services/nd/nd-auth.service';

@Component({
  selector: 'app-nd-reset-password',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  template: `
    <div class="nd-auth-page">
      <div class="nd-card nd-auth-card">
        <h1>Set new password</h1>
        @if (initializing) {
          <p class="nd-muted">Verifying reset link…</p>
        } @else if (initError) {
          <p class="nd-error">{{ initError }}</p>
          <p class="nd-muted center"><a routerLink="/nd/auth/forgot-password">Request a new link</a></p>
        } @else {
          <form (ngSubmit)="submit()">
            <input
              class="nd-input"
              type="password"
              [(ngModel)]="password"
              name="password"
              placeholder="New password"
              minlength="8"
              required
            />
            <input
              class="nd-input"
              type="password"
              [(ngModel)]="confirm"
              name="confirm"
              placeholder="Confirm password"
              minlength="8"
              required
            />
            @if (error) {
              <p class="nd-error">{{ error }}</p>
            }
            @if (message) {
              <p class="nd-muted">{{ message }}</p>
            }
            <button type="submit" class="nd-btn-primary w-full" [disabled]="loading">Update password</button>
          </form>
        }
        <p class="nd-muted center"><a routerLink="/nd/auth/login">Back to sign in</a></p>
      </div>
    </div>
  `,
  styleUrl: './nd-login.component.scss',
})
export class NdResetPasswordComponent implements OnInit {
  private readonly auth = inject(NdAuthService);
  private readonly router = inject(Router);

  password = '';
  confirm = '';
  error = '';
  message = '';
  initError = '';
  loading = false;
  initializing = true;

  async ngOnInit(): Promise<void> {
    const err = await this.auth.establishRecoverySession();
    this.initializing = false;
    if (err) this.initError = err;
  }

  async submit(): Promise<void> {
    if (this.password.length < 6) {
      this.error = 'Password must be at least 6 characters.';
      return;
    }
    if (this.password !== this.confirm) {
      this.error = 'Passwords do not match.';
      return;
    }
    this.error = '';
    this.message = '';
    this.loading = true;
    const err = await this.auth.resetPassword(this.password);
    this.loading = false;
    if (err) {
      this.error = err;
      return;
    }
    this.message = 'Password updated. Sign in with your new password.';
    await this.router.navigate(['/nd/auth/login']);
  }
}
