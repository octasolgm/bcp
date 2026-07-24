import { Component, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { NdAuthService } from '../../../services/nd/nd-auth.service';
import { ThemeService, type ThemeMode } from '../../../services/theme.service';
import { BrandLogoComponent } from '../../../components/brand-logo/brand-logo.component';

@Component({
  selector: 'app-nd-login',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink, BrandLogoComponent],
  templateUrl: './nd-login.component.html',
  styleUrl: './nd-login.component.scss',
})
export class NdLoginComponent {
  private readonly auth = inject(NdAuthService);
  private readonly router = inject(Router);
  readonly theme = inject(ThemeService);

  email = '';
  password = '';
  error = '';
  loading = false;
  showPassword = false;

  async submit(): Promise<void> {
    this.error = '';
    if (!this.email.trim() || !this.password) {
      this.error = 'Enter email and password.';
      return;
    }

    this.loading = true;
    const err = await this.auth.signIn(this.email, this.password);
    this.loading = false;
    if (err) {
      this.error = err;
      return;
    }
    await this.router.navigate(['/nd/overview']);
  }

  setTheme(mode: ThemeMode): void {
    this.theme.setMode(mode);
  }
}
