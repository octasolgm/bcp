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
          <span class="logo">R</span>
          <span>Reguliq <small>.NET</small></span>
        </a>
        <nav class="top-nav">
          <a routerLink="/dashboard" routerLinkActive="active" [routerLinkActiveOptions]="{ exact: false }">Dashboard</a>
          <a routerLink="/dual-verify" routerLinkActive="active">Dual Verify</a>
        </nav>
        <span class="org">SNB UAE / DIFC</span>
        <a routerLink="/dual-verify" class="cta">+ Dual Verify</a>
      </header>
      <div class="body">
        <aside class="sidebar">
          <p class="nav-label">Workspace</p>
          <a routerLink="/dashboard" routerLinkActive="active" [routerLinkActiveOptions]="{ exact: true }">Overview</a>
          <a routerLink="/dual-verify" routerLinkActive="active">Dual Verify</a>
          <p class="nav-label">Regulations</p>
          <span class="disabled">Library (NestJS)</span>
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
