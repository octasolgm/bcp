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

export type NdPageHeaderExportFormatOption = {
  value: string;
  label: string;
};

export type NdPageHeaderExportAction = {
  label: string;
  disabled: boolean;
  run: () => void;
  /** Optional format picker shown beside Export (e.g. Excel vs PDF). */
  format?: {
    value: string;
    options: NdPageHeaderExportFormatOption[];
    onChange: (value: string) => void;
  };
};

export type NdPageHeaderHistoryAction = {
  disabled?: boolean;
  run: () => void;
};

export type NdPageHeaderActionsState = {
  refresh?: NdPageHeaderRefreshAction;
  upload?: NdPageHeaderUploadAction;
  export?: NdPageHeaderExportAction;
  history?: NdPageHeaderHistoryAction;
};

/** Routed ND pages register refresh/upload controls for the shell title bar. */
@Injectable({ providedIn: 'root' })
export class NdPageHeaderActionsService {
  readonly actions = signal<NdPageHeaderActionsState | null>(null);
  /** Center ticker on the blue page header (e.g. New Analysis live progress). */
  readonly marquee = signal<string | null>(null);
  /** Shell header "In progress" nav — only New Analysis opts in. */
  readonly showInProgressNav = signal(false);
  /** When set, replaces the shell blue bar title (e.g. unified gap report matching review workspace). */
  readonly titleOverride = signal<string | null>(null);

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

  setTitleOverride(title: string | null): void {
    const trimmed = title?.trim() ?? '';
    this.titleOverride.set(trimmed ? trimmed : null);
  }

  clear(): void {
    this.clearActions();
    this.clearMarquee();
    this.showInProgressNav.set(false);
    this.titleOverride.set(null);
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
