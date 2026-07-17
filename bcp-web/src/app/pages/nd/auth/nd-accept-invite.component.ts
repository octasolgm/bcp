import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { NdAuthService } from '../../../services/nd/nd-auth.service';

@Component({
  selector: 'app-nd-accept-invite',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  template: `
    <div class="nd-auth-page">
      <div class="nd-card nd-auth-card">
        <h1>Accept invitation</h1>
        @if (initializing) {
          <p class="nd-muted">Verifying invite link…</p>
        } @else if (initError) {
          <p class="nd-error">{{ initError }}</p>
        } @else {
          <form (ngSubmit)="submit()">
            <input class="nd-input" [(ngModel)]="fullName" name="fullName" placeholder="Full name" required />
            <input
              class="nd-input"
              type="password"
              [(ngModel)]="password"
              name="password"
              placeholder="Password"
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
            <button type="submit" class="nd-btn-primary w-full" [disabled]="loading">Complete setup</button>
          </form>
        }
        <p class="nd-muted center"><a routerLink="/nd/auth/login">Sign in</a></p>
      </div>
    </div>
  `,
  styleUrl: './nd-login.component.scss',
})
export class NdAcceptInviteComponent implements OnInit {
  private readonly auth = inject(NdAuthService);
  private readonly router = inject(Router);

  fullName = '';
  password = '';
  confirm = '';
  error = '';
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
    this.loading = true;
    const err = await this.auth.acceptInvite(this.fullName, this.password);
    this.loading = false;
    if (err) this.error = err;
    else await this.router.navigate(['/nd/overview']);
  }
}
