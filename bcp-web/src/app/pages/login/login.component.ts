import { Component, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { AuthService } from '../../services/auth.service';
import { ThemeService, type ThemeMode } from '../../services/theme.service';

@Component({
  selector: 'app-login',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './login.component.html',
  styleUrl: './login.component.scss',
})
export class LoginComponent {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  readonly theme = inject(ThemeService);

  username = '';
  password = '';
  error = '';
  loading = false;
  showPassword = false;

  submit(): void {
    this.error = '';
    if (!this.username.trim() || !this.password) {
      this.error = 'Enter username and password.';
      return;
    }

    this.loading = true;
    window.setTimeout(() => {
      const ok = this.auth.login(this.username, this.password);
      this.loading = false;
      if (!ok) {
        this.error = 'Invalid username or password.';
        return;
      }
      this.router.navigate(['/dashboard']);
    }, 280);
  }

  setTheme(mode: ThemeMode): void {
    this.theme.setMode(mode);
  }
}
