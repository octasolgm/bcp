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
          <span class="logo">R</span> Reguliq <small>.NET</small>
        </a>
        <nav>
          <a routerLink="/dashboard" routerLinkActive="active">Dashboard</a>
          <a routerLink="/dual-verify" routerLinkActive="active">Dual Verify</a>
        </nav>
        <a routerLink="/dual-verify" class="cta">+ Dual Verify</a>
      </header>
      <main class="content">
        <router-outlet />
      </main>
    </div>
  `,
  styles: [`
    .shell { min-height: 100vh; background: #0b111b; color: #e2e8f0; }
    .topbar { display: flex; align-items: center; gap: 2rem; padding: 0 1.5rem; height: 56px; border-bottom: 1px solid rgba(255,255,255,.1); }
    .brand { display: flex; align-items: center; gap: .5rem; font-weight: 600; text-decoration: none; color: inherit; }
    .logo { width: 28px; height: 28px; border-radius: 6px; background: #10b981; color: #fff; display: grid; place-items: center; font-size: 12px; }
    .brand small { color: #64748b; font-weight: 400; }
    nav { display: flex; gap: .5rem; flex: 1; }
    nav a { padding: .4rem .75rem; border-radius: 8px; color: #94a3b8; text-decoration: none; font-size: 14px; }
    nav a.active, nav a:hover { background: #1e293b; color: #fff; }
    .cta { background: #10b981; color: #fff; padding: .4rem .9rem; border-radius: 8px; text-decoration: none; font-size: 14px; }
    .content { padding: 1.5rem; max-width: 1200px; margin: 0 auto; }
  `],
})
export class ShellComponent {}
