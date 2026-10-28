import {
  ChangeDetectionStrategy,
  Component,
  EventEmitter,
  Input,
  OnChanges,
  OnDestroy,
  Output,
  SimpleChanges,
} from '@angular/core';

@Component({
  selector: 'app-nd-page-alert',
  standalone: true,
  template: `
    <div class="nd-page-alert" [class.success]="kind === 'success'" [class.error]="kind === 'error'" role="status">
      <span class="nd-page-alert-text">{{ text }}</span>
      <button type="button" class="nd-page-alert-close" aria-label="Dismiss" (click)="dismissNow()">×</button>
    </div>
  `,
  styles: [
    `
      :host {
        display: block;
        pointer-events: auto;
      }

      .nd-page-alert {
        display: flex;
        align-items: flex-start;
        justify-content: space-between;
        gap: 0.75rem;
        padding: 0.75rem 1rem;
        border-radius: var(--radius-md);
        font-size: 0.8125rem;
        line-height: 1.45;
        box-shadow: 0 10px 28px rgba(15, 23, 42, 0.16);
        animation: nd-page-alert-in 180ms ease-out;
      }

      /* Docked alerts slide in from the right edge instead of pushing page content. */
      @keyframes nd-page-alert-in {
        from {
          opacity: 0;
          transform: translateX(1.5rem);
        }
        to {
          opacity: 1;
          transform: translateX(0);
        }
      }

      @media (prefers-reduced-motion: reduce) {
        .nd-page-alert {
          animation: none;
        }
      }

      .nd-page-alert.success {
        background: var(--low-bg);
        border: 1px solid color-mix(in srgb, var(--low) 35%, transparent);
        color: var(--low);
      }

      .nd-page-alert.error {
        background: var(--critical-bg);
        border: 1px solid color-mix(in srgb, var(--critical) 40%, transparent);
        color: var(--critical);
      }

      .nd-page-alert-text {
        flex: 1;
        min-width: 0;
      }

      .nd-page-alert-close {
        flex-shrink: 0;
        border: none;
        background: transparent;
        color: inherit;
        opacity: 0.72;
        font-size: 1.125rem;
        line-height: 1;
        padding: 0.125rem 0.25rem;
        cursor: pointer;
        border-radius: var(--radius-sm);
      }

      .nd-page-alert-close:hover {
        opacity: 1;
        background: color-mix(in srgb, currentColor 12%, transparent);
      }
    `,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class NdPageAlertComponent implements OnChanges, OnDestroy {
  @Input({ required: true }) text!: string;
  @Input() kind: 'success' | 'error' = 'success';
  /** Auto-hide after ms; 0 = stay until closed. */
  @Input() autoHideMs = 6000;

  @Output() dismiss = new EventEmitter<void>();

  private hideTimer: ReturnType<typeof setTimeout> | null = null;

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['text'] && this.text) {
      this.scheduleAutoHide();
    }
  }

  ngOnDestroy(): void {
    this.clearTimer();
  }

  dismissNow(): void {
    this.clearTimer();
    this.dismiss.emit();
  }

  private scheduleAutoHide(): void {
    this.clearTimer();
    if (this.autoHideMs > 0) {
      this.hideTimer = setTimeout(() => this.dismissNow(), this.autoHideMs);
    }
  }

  private clearTimer(): void {
    if (this.hideTimer) {
      clearTimeout(this.hideTimer);
      this.hideTimer = null;
    }
  }
}
