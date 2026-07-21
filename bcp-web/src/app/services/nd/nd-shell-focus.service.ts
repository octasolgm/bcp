import { Injectable, signal } from '@angular/core';

/** Collapses ND sidebar when a child page needs focus (e.g. regulation points panel). */
@Injectable({ providedIn: 'root' })
export class NdShellFocusService {
  private readonly _regulationPointsPanelOpen = signal(false);

  readonly regulationPointsPanelOpen = this._regulationPointsPanelOpen.asReadonly();

  setRegulationPointsPanelOpen(open: boolean): void {
    this._regulationPointsPanelOpen.set(open);
  }
}
