import { Injectable, signal } from '@angular/core';

export type NdStepState = 'done' | 'active' | 'pending';

export type NdStep = {
  label: string;
  state: NdStepState;
};

/**
 * Backing store for the left-side vertical step tracker on the New Analysis page — mirrors
 * NdPipelinePanelService's pattern (see that file): the page component pushes its own progress
 * in via activate()/setSteps(), and the shell reads it reactively to render the rail without
 * owning any page state itself.
 */
@Injectable({ providedIn: 'root' })
export class NdStepTrackerService {
  private static readonly MIN_WIDTH = 160;
  private static readonly MAX_WIDTH = 360;
  private static readonly DEFAULT_WIDTH = 200;

  private readonly _active = signal(false);
  private readonly _steps = signal<NdStep[]>([]);
  // Collapse/width are session-only by design — not persisted, so a refresh always starts from
  // the default layout instead of carrying over whatever size was last dragged to.
  private readonly _collapsed = signal(false);
  private readonly _width = signal(NdStepTrackerService.DEFAULT_WIDTH);

  readonly active = this._active.asReadonly();
  readonly steps = this._steps.asReadonly();
  readonly collapsed = this._collapsed.asReadonly();
  readonly width = this._width.asReadonly();

  /** Called by the host page on init — makes the shell show the tracker rail. */
  activate(): void {
    this._active.set(true);
  }

  /** Called on destroy so the rail disappears when navigating away. */
  deactivate(): void {
    this._active.set(false);
    this._steps.set([]);
  }

  setSteps(steps: NdStep[]): void {
    this._steps.set(steps);
  }

  toggleCollapsed(): void {
    this._collapsed.update((v) => !v);
  }

  setWidth(px: number): void {
    const clamped = Math.round(Math.min(NdStepTrackerService.MAX_WIDTH, Math.max(NdStepTrackerService.MIN_WIDTH, px)));
    this._width.set(clamped);
  }
}
