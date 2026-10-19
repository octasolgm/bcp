import { Component, ElementRef, HostListener, ViewChild, computed, effect, inject, input, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { NdStepTrackerService } from '../../services/nd/nd-step-tracker.service';

/** Left-side vertical progress rail for the New Analysis page — see NdStepTrackerService. */
@Component({
  selector: 'app-nd-step-tracker',
  standalone: true,
  imports: [CommonModule],
  host: { '[class.phone]': 'phone()' },
  template: `
    @if (phone() && phoneOpen()) {
      <div class="phone-backdrop" (click)="closePhone()" aria-hidden="true"></div>
    }
    <aside
      class="step-tracker"
      [class.collapsed]="collapsed()"
      [class.phone-open]="phone() && phoneOpen()"
      [style.width.px]="collapsed() || phone() ? null : stepTracker.width()"
      [style.min-width.px]="collapsed() || phone() ? null : stepTracker.width()"
    >
      @if (!collapsed() && !phone()) {
        <div
          class="tracker-resize-handle"
          role="separator"
          aria-orientation="vertical"
          aria-label="Resize progress tracker"
          title="Drag to resize"
          (pointerdown)="startResize($event)"
        ></div>
      }
      <button
        #toggleBtn
        type="button"
        class="tracker-toggle"
        (click)="toggle()"
        [attr.aria-label]="collapsed() ? 'Show progress tracker' : 'Hide progress tracker'"
        [attr.aria-pressed]="!collapsed()"
      >
        <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
          @if (collapsed()) {
            <polyline points="9 18 15 12 9 6" />
          } @else {
            <polyline points="15 18 9 12 15 6" />
          }
        </svg>
        @if (collapsed()) {
          <span class="tracker-toggle-label">Progress</span>
        }
      </button>

      @if (!collapsed()) {
        <h3 class="step-tracker-title">Progress</h3>
        <ol class="step-list">
          @for (step of stepTracker.steps(); track step.label; let last = $last) {
            <li class="step-row" [class]="'state-' + step.state">
              <div class="step-marker">
                <span class="step-dot">
                  @if (step.state === 'done') {
                    <svg width="11" height="11" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="3">
                      <polyline points="20 6 9 17 4 12" />
                    </svg>
                  }
                </span>
                @if (!last) {
                  <span class="step-line"></span>
                }
              </div>
              <span class="step-label">{{ step.label }}</span>
            </li>
          }
        </ol>
      }
    </aside>
  `,
  styleUrl: './nd-step-tracker.component.scss',
})
export class NdStepTrackerComponent {
  readonly stepTracker = inject(NdStepTrackerService);

  /** Phone layout (the shell's PHONE_QUERY): the rail stays collapsed by default and opening it
   * shows the tracker as an overlay with a backdrop, so the page keeps its width. Tracked apart
   * from the service's collapsed flag so the tablet/desktop state is left as it was. */
  readonly phone = input(false);
  readonly phoneOpen = signal(false);
  readonly collapsed = computed(() => (this.phone() ? !this.phoneOpen() : this.stepTracker.collapsed()));

  @ViewChild('toggleBtn') private toggleBtn?: ElementRef<HTMLButtonElement>;

  private readonly resetPhoneOpen = effect(() => {
    if (!this.phone()) this.phoneOpen.set(false);
  });

  private resizeCleanup: (() => void) | null = null;

  toggle(): void {
    if (this.phone()) this.phoneOpen.update((v) => !v);
    else this.stepTracker.toggleCollapsed();
  }

  closePhone(restoreFocus = false): void {
    if (!this.phoneOpen()) return;
    this.phoneOpen.set(false);
    if (restoreFocus) this.toggleBtn?.nativeElement.focus();
  }

  @HostListener('document:keydown.escape')
  onEscape(): void {
    this.closePhone(true);
  }

  startResize(event: PointerEvent): void {
    event.preventDefault();
    this.resizeCleanup?.();
    const startX = event.clientX;
    const startWidth = this.stepTracker.width();
    const move = (e: PointerEvent) => {
      this.stepTracker.setWidth(startWidth + (e.clientX - startX));
    };
    const up = () => {
      this.resizeCleanup?.();
    };
    document.addEventListener('pointermove', move);
    document.addEventListener('pointerup', up);
    this.resizeCleanup = () => {
      document.removeEventListener('pointermove', move);
      document.removeEventListener('pointerup', up);
      this.resizeCleanup = null;
    };
  }
}
