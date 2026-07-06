import { Component } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

@Component({
  selector: 'app-shell',
  standalone: true,
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  template: `
    <div class="shell">
      <header class="topbar">
        <a routerLink="/dashboard" class="brand">
          <span class="logo">B</span>
          <span>BCP App <small>Compliance</small></span>
        </a>
        <span class="org">SNB UAE / DIFC</span>
      </header>
      <div class="body">
        <aside class="sidebar">
          <p class="nav-label">Workspace</p>
          <a routerLink="/dashboard" routerLinkActive="active" [routerLinkActiveOptions]="{ exact: true }">Dashboard</a>
          <a routerLink="/dual-verify" routerLinkActive="active">Dual Verify</a>
        </aside>
        <main class="content">
          <router-outlet />
        </main>
      </div>
    </div>
  `,
  styleUrl: './shell.component.scss',
})
export class ShellComponent {}
