import { Component, HostListener, OnDestroy, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { NavigationEnd, Router, RouterLink } from '@angular/router';
import { filter, Subscription } from 'rxjs';
import { NdApiService } from '../../../services/nd/nd-api.service';
import { NdSupportCenterComponent } from './nd-support-center.component';

/** How often the Support button checks for new replies while the panel is closed. */
const UNREAD_POLL_MS = 60_000;

/**
 * Floating "Support" button shown on every page for real (non-demo) workspace users. Opens a side panel with
 * the user's support requests and the chat with the platform team. The shell decides who sees it.
 */
@Component({
  selector: 'app-nd-support-widget',
  standalone: true,
  imports: [CommonModule, RouterLink, NdSupportCenterComponent],
  template: `
    @if (!onSupportPage) {
      <button
        type="button"
        class="sw-fab"
        [class.open]="open"
        (click)="toggle()"
        [attr.aria-expanded]="open"
        aria-controls="nd-support-panel"
        title="Contact support"
      >
        <svg viewBox="0 0 20 20" fill="none" stroke="currentColor" stroke-width="1.6" aria-hidden="true">
          <path d="M4 4.5h12a1.5 1.5 0 0 1 1.5 1.5v7a1.5 1.5 0 0 1-1.5 1.5H9l-3.5 3v-3H4A1.5 1.5 0 0 1 2.5 13V6A1.5 1.5 0 0 1 4 4.5z" />
          <path d="M6.5 8.5h7M6.5 11h4.5" />
        </svg>
        <span>Support</span>
        @if (unread > 0) {
          <span class="sw-badge" [attr.aria-label]="unread + ' unread'">{{ unread > 9 ? '9+' : unread }}</span>
        }
      </button>
    }

    @if (open && !onSupportPage) {
      <div class="sw-backdrop" (click)="close()" aria-hidden="true"></div>
      <aside id="nd-support-panel" class="sw-panel" role="dialog" aria-label="Contact support">
        <header class="sw-head">
          <strong>Contact support</strong>
          <span class="sw-head-actions">
            <a routerLink="/nd/support" (click)="close()" class="sw-link">Open full page</a>
            <button type="button" class="sw-close" (click)="close()" aria-label="Close">&times;</button>
          </span>
        </header>
        <div class="sw-body">
          <app-nd-support-center mode="panel" (unreadChanged)="refreshUnread()" />
        </div>
      </aside>
    }
  `,
  styles: [
    `
      .sw-fab {
        position: fixed;
        right: 20px;
        bottom: 20px;
        z-index: 900;
        display: inline-flex;
        align-items: center;
        gap: 0.45rem;
        padding: 0.6rem 1rem;
        border: none;
        border-radius: 9999px;
        background: var(--accent);
        color: #fff;
        font-size: 0.875rem;
        font-weight: 600;
        cursor: pointer;
        box-shadow: 0 6px 20px rgba(15, 23, 42, 0.22);
      }
      .sw-fab svg {
        width: 18px;
        height: 18px;
      }
      .sw-fab.open {
        opacity: 0;
        pointer-events: none;
      }
      .sw-badge {
        min-width: 18px;
        height: 18px;
        padding: 0 5px;
        border-radius: 9999px;
        background: #dc2626;
        color: #fff;
        font-size: 0.7rem;
        line-height: 18px;
        text-align: center;
      }
      .sw-backdrop {
        position: fixed;
        inset: 0;
        z-index: 950;
        background: rgba(15, 23, 42, 0.25);
      }
      .sw-panel {
        position: fixed;
        top: 0;
        right: 0;
        bottom: 0;
        z-index: 960;
        width: min(440px, 100vw);
        display: flex;
        flex-direction: column;
        background: var(--surface, var(--bg-card));
        border-left: 1px solid var(--border);
        box-shadow: -8px 0 30px rgba(15, 23, 42, 0.18);
      }
      .sw-head {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 0.75rem;
        padding: 0.85rem 1rem;
        border-bottom: 1px solid var(--border);
        color: var(--text-primary);
      }
      .sw-head-actions {
        display: inline-flex;
        align-items: center;
        gap: 0.75rem;
      }
      .sw-link {
        font-size: 0.8rem;
        color: var(--accent);
        text-decoration: none;
      }
      .sw-close {
        background: none;
        border: none;
        font-size: 1.4rem;
        line-height: 1;
        cursor: pointer;
        color: var(--text-muted);
      }
      .sw-body {
        flex: 1 1 auto;
        min-height: 0;
        overflow-y: auto;
        padding: 1rem;
      }
      .sw-body app-nd-support-center {
        height: 100%;
      }
      @media (max-width: 600px) {
        .sw-fab span:not(.sw-badge) {
          display: none;
        }
      }
    `,
  ],
})
export class NdSupportWidgetComponent implements OnInit, OnDestroy {
  private readonly api = inject(NdApiService);
  private readonly router = inject(Router);

  open = false;
  unread = 0;
  onSupportPage = false;

  private timer: ReturnType<typeof setInterval> | null = null;
  private navSub?: Subscription;

  ngOnInit(): void {
    this.onSupportPage = this.isSupportUrl(this.router.url);
    this.navSub = this.router.events.pipe(filter((e) => e instanceof NavigationEnd)).subscribe(() => {
      this.onSupportPage = this.isSupportUrl(this.router.url);
      if (this.onSupportPage) this.open = false;
    });
    void this.refreshUnread();
    this.timer = setInterval(() => {
      if (!document.hidden) void this.refreshUnread();
    }, UNREAD_POLL_MS);
  }

  ngOnDestroy(): void {
    if (this.timer) clearInterval(this.timer);
    this.navSub?.unsubscribe();
  }

  @HostListener('document:keydown.escape')
  onEscape(): void {
    if (this.open) this.close();
  }

  toggle(): void {
    this.open = !this.open;
  }

  close(): void {
    this.open = false;
    void this.refreshUnread();
  }

  async refreshUnread(): Promise<void> {
    const res = await this.api.getSupportUnread();
    if (res.success && res.data) this.unread = res.data.count;
  }

  private isSupportUrl(url: string): boolean {
    return url.split('?')[0].startsWith('/nd/support');
  }
}
