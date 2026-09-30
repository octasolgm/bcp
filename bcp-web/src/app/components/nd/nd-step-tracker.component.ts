import { Component, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { NdStepTrackerService } from '../../services/nd/nd-step-tracker.service';

/** Left-side vertical progress rail for the New Analysis page — see NdStepTrackerService. */
@Component({
  selector: 'app-nd-step-tracker',
  standalone: true,
  imports: [CommonModule],
  template: `
    <aside
      class="step-tracker"
      [class.collapsed]="stepTracker.collapsed()"
      [style.width.px]="stepTracker.collapsed() ? null : stepTracker.width()"
      [style.min-width.px]="stepTracker.collapsed() ? null : stepTracker.width()"
    >
      @if (!stepTracker.collapsed()) {
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
        type="button"
        class="tracker-toggle"
        (click)="stepTracker.toggleCollapsed()"
        [attr.aria-label]="stepTracker.collapsed() ? 'Show progress tracker' : 'Hide progress tracker'"
        [attr.aria-pressed]="!stepTracker.collapsed()"
      >
        <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
          @if (stepTracker.collapsed()) {
            <polyline points="9 18 15 12 9 6" />
          } @else {
            <polyline points="15 18 9 12 15 6" />
          }
        </svg>
        @if (stepTracker.collapsed()) {
          <span class="tracker-toggle-label">Progress</span>
        }
      </button>

      @if (!stepTracker.collapsed()) {
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

  private resizeCleanup: (() => void) | null = null;

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
