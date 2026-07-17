import { Component, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { NdAuthService } from '../../../services/nd/nd-auth.service';

@Component({
  selector: 'app-nd-forgot-password',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  template: `
    <div class="nd-auth-page">
      <div class="nd-card nd-auth-card">
        <h1>Forgot password</h1>
        <form (ngSubmit)="submit()">
          <input class="nd-input" type="email" [(ngModel)]="email" name="email" placeholder="Email" required />
          @if (message) { <p class="nd-muted">{{ message }}</p> }
          @if (resetLink) {
            <p class="nd-muted">
              <a [href]="resetLink" target="_blank" rel="noopener">Open password reset link</a>
            </p>
          }
          @if (error) { <p class="nd-error">{{ error }}</p> }
          <button type="submit" class="nd-btn-primary w-full" [disabled]="loading">Send reset link</button>
        </form>
        <p class="nd-muted center"><a routerLink="/nd/auth/login">Back to sign in</a></p>
      </div>
    </div>
  `,
  styleUrl: './nd-login.component.scss',
})
export class NdForgotPasswordComponent {
  private readonly auth = inject(NdAuthService);
  email = '';
  error = '';
  message = '';
  resetLink = '';
  loading = false;

  async submit(): Promise<void> {
    this.error = '';
    this.message = '';
    this.resetLink = '';
    this.loading = true;
    const res = await this.auth.forgotPassword(this.email);
    this.loading = false;
    if (res.error) this.error = res.error;
    else {
      this.message = res.resetLink
        ? 'Development mode: use the link below to reset your password.'
        : 'Check your email for a reset link.';
      this.resetLink = res.resetLink ?? '';
    }
  }
}
