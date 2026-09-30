import { Injectable, signal } from '@angular/core';

export type NdPageHeaderRefreshAction = {
  disabled: boolean;
  run: () => void;
};

export type NdPageHeaderUploadAction = {
  label: string;
  disabled: boolean;
  run: () => void;
};

export type NdPageHeaderActionsState = {
  refresh?: NdPageHeaderRefreshAction;
  upload?: NdPageHeaderUploadAction;
};

/** Routed ND pages register refresh/upload controls for the shell title bar. */
@Injectable({ providedIn: 'root' })
export class NdPageHeaderActionsService {
  readonly actions = signal<NdPageHeaderActionsState | null>(null);
  /** Center ticker on the blue page header (e.g. New Analysis live progress). */
  readonly marquee = signal<string | null>(null);
  /** Shell header "In progress" nav — only New Analysis opts in. */
  readonly showInProgressNav = signal(false);

  set(state: NdPageHeaderActionsState | null): void {
    this.actions.set(state);
  }

  setMarquee(text: string | null): void {
    const trimmed = text?.trim() ?? '';
    this.marquee.set(trimmed ? trimmed : null);
  }

  clearActions(): void {
    this.actions.set(null);
  }

  clearMarquee(): void {
    this.marquee.set(null);
  }

  setShowInProgressNav(show: boolean): void {
    this.showInProgressNav.set(show);
  }

  clear(): void {
    this.clearActions();
    this.clearMarquee();
    this.showInProgressNav.set(false);
  }
}

export function syncCatalogPageHeaderActions(
  service: NdPageHeaderActionsService,
  opts: {
    showRefresh: boolean;
    loading: boolean;
    onRefresh: () => void;
    showUpload: boolean;
    uploading: boolean;
    uploadLabel: string;
    onUpload: () => void;
  },
): void {
  if (!opts.showRefresh && !opts.showUpload) {
    service.clearActions();
    return;
  }
  const state: NdPageHeaderActionsState = {};
  if (opts.showRefresh) {
    state.refresh = { disabled: opts.loading, run: opts.onRefresh };
  }
  if (opts.showUpload) {
    state.upload = {
      label: opts.uploading ? 'Uploading…' : opts.uploadLabel,
      disabled: opts.uploading,
      run: opts.onUpload,
    };
  }
  service.set(state);
}
