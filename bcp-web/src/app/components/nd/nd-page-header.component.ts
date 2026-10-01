import { Component, EventEmitter, Input, Output, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { NdPageHeaderActionsService } from '../../services/nd/nd-page-header-actions.service';
import { InProgressNavButtonComponent } from '../in-progress-nav-button/in-progress-nav-button.component';

/** Fixed, colored title bar shown at the top of every ND page's content area — stays in place
 * while the page body scrolls beneath it (see nd-shell.component.html/.scss, which renders this
 * once and drives `title`/`showBack` from the active route, so no individual page has to wire
 * this up itself). */
@Component({
  selector: 'app-nd-page-header',
  standalone: true,
  imports: [CommonModule, InProgressNavButtonComponent],
  template: `
    <header class="nd-page-header">
      <div class="nd-page-header-main">
        @if (showBack) {
          <button type="button" class="nd-page-header-back" (click)="back.emit()" aria-label="Go back">
            <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
              <polyline points="15 18 9 12 15 6" />
            </svg>
          </button>
        }
        <h1 class="nd-page-header-title">{{ titleOverride() || title }}</h1>
      </div>
      @if (marqueeText(); as ticker) {
        <div class="nd-page-header-marquee" aria-live="polite">
          <div class="nd-page-header-marquee-track">
            <span class="nd-page-header-marquee-text">{{ ticker }}</span>
            <span class="nd-page-header-marquee-text" aria-hidden="true">{{ ticker }}</span>
          </div>
        </div>
      }
      <div class="nd-page-header-actions">
        @if (headerActions(); as toolbar) {
          @if (toolbar.upload) {
            <button
              type="button"
              class="nd-page-header-upload-btn"
              (click)="toolbar.upload.run()"
              [disabled]="toolbar.upload.disabled"
            >
              {{ toolbar.upload.label }}
            </button>
          }
          @if (toolbar.export) {
            <div class="nd-page-header-export-group">
              @if (toolbar.export.format) {
                <label class="nd-page-header-export-format-wrap">
                  <span class="visually-hidden">Export format</span>
                  <select
                    class="nd-page-header-export-format"
                    [value]="toolbar.export.format.value"
                    [disabled]="toolbar.export.disabled"
                    (change)="toolbar.export.format.onChange($any($event.target).value)"
                  >
                    @for (opt of toolbar.export.format.options; track opt.value) {
                      <option [value]="opt.value">{{ opt.label }}</option>
                    }
                  </select>
                </label>
              }
              <button
                type="button"
                class="nd-page-header-upload-btn"
                (click)="toolbar.export.run()"
                [disabled]="toolbar.export.disabled"
              >
                {{ toolbar.export.label }}
              </button>
            </div>
          }
          @if (toolbar.history) {
            <button
              type="button"
              class="nd-page-header-icon-btn"
              (click)="toolbar.history.run()"
              [disabled]="toolbar.history.disabled"
              aria-label="Run history"
              title="Run history"
            >
              <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" aria-hidden="true">
                <path d="M3 12a9 9 0 1 0 9-9 9 9 0 0 0-6.36 2.64" />
                <path d="M3 3v6h6" />
                <path d="M12 7v5l3 2" />
              </svg>
            </button>
          }
          @if (toolbar.refresh) {
            <button
              type="button"
              class="nd-page-header-icon-btn"
              (click)="toolbar.refresh.run()"
              [disabled]="toolbar.refresh.disabled"
              aria-label="Refresh"
              title="Refresh"
            >
              <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" aria-hidden="true">
                <path d="M21 12a9 9 0 1 1-2.64-6.36" />
                <path d="M21 3v6h-6" />
              </svg>
            </button>
          }
        }
        @if (showInProgressNav()) {
          <app-in-progress-nav-button variant="header" />
        }
      </div>
    </header>
  `,
  styleUrl: './nd-page-header.component.scss',
})
export class NdPageHeaderComponent {
  private readonly actionsService = inject(NdPageHeaderActionsService);

  @Input() title = '';
  @Input() showBack = false;
  @Output() back = new EventEmitter<void>();

  readonly headerActions = this.actionsService.actions;
  readonly marqueeText = this.actionsService.marquee;
  readonly showInProgressNav = this.actionsService.showInProgressNav;
  readonly titleOverride = this.actionsService.titleOverride;
}
