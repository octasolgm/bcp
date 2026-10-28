import { Injectable } from '@angular/core';
import { Subject } from 'rxjs';
import type { NdNavBadgeBumps } from '../../../lib/nd/nav-badge-bumps';
import type { AnalysisRunSummary } from '../../../lib/nd/types';

/** Signals ND shell to refresh sidebar nav-counts (e.g. after deleting a run). */
@Injectable({ providedIn: 'root' })
export class NdWorkspaceNavService {
  private readonly refreshRequested$ = new Subject<void>();
  private readonly bumps$ = new Subject<NdNavBadgeBumps>();
  private readonly analysisRunSoftDeleted$ = new Subject<AnalysisRunSummary>();
  private readonly analysisRunPermanentlyDeleted$ = new Subject<AnalysisRunSummary>();

  readonly refreshRequested = this.refreshRequested$.asObservable();
  readonly bumps = this.bumps$.asObservable();
  readonly analysisRunSoftDeleted = this.analysisRunSoftDeleted$.asObservable();
  readonly analysisRunPermanentlyDeleted = this.analysisRunPermanentlyDeleted$.asObservable();

  requestNavBadgeRefresh(): void {
    this.refreshRequested$.next();
  }

  /** Adjust sidebar badges immediately; API refresh still runs to reconcile. */
  bumpNavBadges(bumps: NdNavBadgeBumps): void {
    this.bumps$.next(bumps);
    this.refreshRequested$.next();
  }

  notifyAnalysisRunSoftDeleted(run: AnalysisRunSummary): void {
    this.analysisRunSoftDeleted$.next(run);
  }

  notifyAnalysisRunPermanentlyDeleted(run: AnalysisRunSummary): void {
    this.analysisRunPermanentlyDeleted$.next(run);
  }
}
