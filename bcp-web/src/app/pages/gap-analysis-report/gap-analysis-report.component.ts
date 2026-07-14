import { Component, inject, OnDestroy, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { forkJoin, of } from 'rxjs';
import { catchError, map } from 'rxjs/operators';
import { downloadExcelRows } from '../../../lib/ai-lab/excel-write';
import {
  progressPointToReportItem,
  savedResultToReportItem,
  type DualVerifyReportItem,
} from '../../../lib/dual-verify-report';
import {
  ApiService,
  type DualVerifySessionSummary,
} from '../../services/api.service';
import { reportItemsToGapItems } from '../../services/gap-analysis-mapper';
import {
  clearGapDrafts,
  clearGapItems,
  loadGapDrafts,
  loadGapItems,
  saveGapDrafts,
  type GapDraftOverlay,
  type GapItemData,
} from '../../services/reguliq-store';
import { ToastService } from '../../services/toast.service';

/** Seeded TFS × IMPTFS combined compliance session (32 points). */
const SEEDED_COMPLIANCE_SESSION = 'a339de5e-06b9-4067-bd97-e7d8086bf31e';

@Component({
  selector: 'app-gap-analysis-report',
  standalone: true,
  imports: [FormsModule, RouterLink],
  templateUrl: './gap-analysis-report.component.html',
  styleUrl: './gap-analysis-report.component.scss',
})
export class GapAnalysisReportComponent implements OnInit, OnDestroy {
  private readonly toast = inject(ToastService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly api = inject(ApiService);
  private saveTimer: ReturnType<typeof setTimeout> | null = null;

  exporting = false;
  loading = true;
  deletingSession = false;
  loadError: string | null = null;
  sourceLabel = 'I M P T F S.pdf vs. TFS Guidelines';
  sessionKey = '';
  /** Raw session id for delete API (from ?session= or ?saved=compliance:…) */
  deletableSessionId: string | null = null;
  deletableSessionKind: 'dual' | 'compliance' | null = null;
  pointIds: string[] = [];
  pdfPreview: { title: string; page: string; body: string } | null = null;

  activeFilter = 'all';

  readonly filters = [
    { id: 'all', label: 'All' },
    { id: 'critical', label: 'Critical' },
    { id: 'high', label: 'High' },
    { id: 'medium', label: 'Medium' },
    { id: 'low', label: 'Low' },
    { id: 'compliant', label: 'Compliant' },
  ];

  items: GapItemData[] = [];

  ngOnInit(): void {
    // Drop old localStorage demo gaps (§2.1 / §2.3 placeholders).
    const loaded = loadGapItems();
    const looksLikeDemo = loaded.some(
      (i) =>
        i.id === '01' &&
        i.section === '§2.1' &&
        /Senior Management SCP Approval/i.test(i.title),
    );
    if (looksLikeDemo) clearGapItems();

    this.route.queryParamMap.subscribe((params) => {
      const filter = params.get('filter');
      if (filter && this.filters.some((f) => f.id === filter)) {
        this.activeFilter = filter;
      }

      const session = params.get('session');
      const saved = params.get('saved');
      this.loadFromQuery(session, saved, params.get('section'), params.get('focus'));
    });
  }

  get summary() {
    return {
      critical: this.items.filter((i) => i.severity === 'critical').length,
      high: this.items.filter((i) => i.severity === 'high').length,
      medium: this.items.filter((i) => i.severity === 'medium').length,
      compliant: this.items.filter((i) => i.severity === 'compliant').length,
    };
  }

  get filteredItems(): GapItemData[] {
    if (this.activeFilter === 'all') return this.items;
    return this.items.filter((i) => i.severity === this.activeFilter);
  }

  get subtitle(): string {
    if (this.loading) return 'Loading analysis results…';
    if (this.loadError) return this.sourceLabel;
    const n = this.items.length;
    return `${this.sourceLabel} — ${n} finding${n === 1 ? '' : 's'}`;
  }

  get canDeleteSession(): boolean {
    return !!this.deletableSessionId && !!this.deletableSessionKind && !this.deletingSession;
  }

  confirmDeleteSession(): void {
    if (!this.deletableSessionId || !this.deletableSessionKind) return;
    const label = this.sourceLabel || this.deletableSessionId;
    const ok = window.confirm(
      `Delete analysis session "${label}" permanently?\n\nThis removes the session from the database. Draft edits for this report will also be cleared.`,
    );
    if (!ok) return;

    this.deletingSession = true;
    const id = this.deletableSessionId;
    const kind = this.deletableSessionKind;
    const key = this.sessionKey;

    const onDone = (message: string) => {
      this.deletingSession = false;
      if (key) clearGapDrafts(key);
      this.toast.show(message, 'success');
      this.router.navigate(['/gap-analysis']);
    };

    const onFail = (message: string) => {
      this.deletingSession = false;
      this.toast.show(message, 'error');
    };

    if (kind === 'compliance') {
      this.api.deleteComplianceSession(id).subscribe({
        next: () => onDone('Compliance session deleted'),
        error: (e) => onFail(e?.error?.message ?? 'Could not delete compliance session'),
      });
      return;
    }

    this.api.deleteDualVerifySession(id).subscribe({
      next: () => onDone('Analysis session deleted'),
      error: () => onDone('Session removed (may already be gone on server)'),
    });
  }

  setFilter(id: string): void {
    this.activeFilter = id;
  }

  toggleItem(item: GapItemData): void {
    item.expanded = !item.expanded;
    this.persistSoon();
  }

  onFieldChange(): void {
    this.persistSoon();
  }

  openPdf(kind: 'reg' | 'policy', item: GapItemData): void {
    this.pdfPreview = {
      title: kind === 'reg' ? 'Regulatory requirement source' : 'Policy extract source',
      page: kind === 'reg' ? item.regPage : item.policyPage,
      body: kind === 'reg' ? item.regulatoryText : item.policyText,
    };
  }

  closePdf(): void {
    this.pdfPreview = null;
  }

  async exportXlsx(): Promise<void> {
    if (this.exporting) return;
    this.exporting = true;
    try {
      const headers = [
        'ID',
        'Section',
        'Title',
        'Severity',
        'Regulatory Requirement',
        'Policy Extract',
        'Gaps Identified',
        'Management Response',
        'Design Effectiveness',
        'Operating Effectiveness',
        'Overall Effectiveness',
        'Document Reference',
        'Evidence',
      ];
      const rows = this.items.map((i) => [
        i.id,
        i.section,
        i.title,
        i.severity,
        i.regulatoryText,
        i.policyText,
        i.gaps,
        i.managementResponse,
        i.designEffectiveness,
        i.operatingEffectiveness,
        i.overallEffectiveness,
        i.documentReference,
        i.evidence,
      ]);
      await downloadExcelRows(
        `reguliq-gap-analysis-${new Date().toISOString().slice(0, 10)}.xlsx`,
        'Gap Analysis',
        headers,
        rows,
        [8, 10, 36, 12, 40, 40, 36, 36, 18, 18, 18, 22, 36],
      );
      this.toast.show('Exported gap analysis Excel file', 'success');
    } catch {
      this.toast.show('Export failed — try again', 'error');
    } finally {
      this.exporting = false;
    }
  }

  ngOnDestroy(): void {
    if (this.saveTimer) clearTimeout(this.saveTimer);
    this.persistDrafts();
  }

  private loadFromQuery(
    session: string | null,
    saved: string | null,
    section: string | null,
    focus: string | null,
  ): void {
    this.loading = true;
    this.loadError = null;
    this.items = [];
    this.pointIds = [];
    this.deletableSessionId = null;
    this.deletableSessionKind = null;

    if (session) {
      this.sessionKey = `session:${session}`;
      this.deletableSessionId = session;
      this.deletableSessionKind = 'dual';
      this.sourceLabel = 'Dual-verify session';
      this.api
        .getJob(session)
        .pipe(catchError(() => this.api.getNestJob(session)))
        .subscribe({
          next: (r) => {
            if (!r?.data?.session) {
              this.loading = false;
              this.loadError =
                'This analysis session was deleted or is no longer available.';
              return;
            }
            const points = r.data?.points ?? [];
            const report = points
              .filter(
                (p) =>
                  p.status === 'completed' ||
                  p.status === 'failed' ||
                  p.agreementJson ||
                  p.landingMessage ||
                  p.llmMessage,
              )
              .map((p) =>
                progressPointToReportItem({
                  pointId: p.pointId,
                  pointTitle: p.pointTitle,
                  status: p.status,
                  landingMessage: p.landingMessage,
                  llmMessage: p.llmMessage,
                  agreementJson: p.agreementJson as DualVerifyReportItem['agreement'],
                  errorMessage: p.errorMessage,
                }),
              );
            this.applyReport(report, section, focus);
          },
          error: () => {
            this.loading = false;
            this.loadError =
              'Could not load this analysis session. It may have been deleted — try opening from Documents again or run a new analysis on V2.';
            this.toast.show(this.loadError, 'error');
          },
        });
      return;
    }

    if (saved?.startsWith('compliance:')) {
      const id = saved.slice('compliance:'.length);
      this.sessionKey = `compliance:${id}`;
      this.deletableSessionId = id;
      this.deletableSessionKind = 'compliance';
      this.sourceLabel = 'I M P T F S.pdf vs. TFS Guidelines';
      this.api.loadComplianceSession(id).subscribe({
        next: (r) => {
          const report: DualVerifyReportItem[] = [];
          for (const row of (r.results as Record<string, unknown>[]) ?? []) {
            const item = savedResultToReportItem(
              row as Parameters<typeof savedResultToReportItem>[0],
            );
            if (item) report.push(item);
          }
          this.applyReport(report, section, focus);
        },
        error: () => {
          this.loading = false;
          this.loadError = 'Could not load compliance session.';
          this.toast.show(this.loadError, 'error');
        },
      });
      return;
    }

    // Default: latest completed dual-verify, else seeded compliance bundle.
    this.resolveDefaultSession(section, focus);
  }

  private resolveDefaultSession(section: string | null, focus: string | null): void {
    forkJoin({
      dual: this.api.listDualVerifySessions().pipe(
        map((r) => r.data ?? []),
        catchError(() => of([] as DualVerifySessionSummary[])),
      ),
      compliance: this.api.listComplianceSessions().pipe(
        map((r) => r.sessions ?? []),
        catchError(() => of([])),
      ),
    }).subscribe({
      next: ({ dual, compliance }) => {
        // Prefer the full TFS × IMPTFS compliance bundle (or richest compliance
        // session) — not a recent partial dual-verify smoke run (1–25 pts).
        const seeded =
          compliance.find((s) => s.id === SEEDED_COMPLIANCE_SESSION) ??
          [...compliance]
            .filter((s) => (s.comparedPoints ?? 0) > 0)
            .sort((a, b) => (b.comparedPoints ?? 0) - (a.comparedPoints ?? 0))[0];

        const bestCompliancePts = seeded?.comparedPoints ?? 0;
        const latestDual = [...dual]
          .filter(
            (s) =>
              s.transport !== 'db' &&
              s.status === 'completed' &&
              s.completedPoints > 0,
          )
          .sort((a, b) => (b.completedPoints ?? 0) - (a.completedPoints ?? 0))[0];

        const dualPts = latestDual?.completedPoints ?? 0;
        if (seeded && bestCompliancePts >= dualPts) {
          this.trySeededCompliance(compliance, section, focus);
          return;
        }

        if (latestDual) {
          this.sessionKey = `session:${latestDual.id}`;
          this.deletableSessionId = latestDual.id;
          this.deletableSessionKind = 'dual';
          this.sourceLabel =
            latestDual.label || 'I M P T F S.pdf vs. TFS Guidelines';
          this.api.getJob(latestDual.id).subscribe({
            next: (r) => {
              const points = r.data?.points ?? [];
              const report = points
                .filter((p) => p.status === 'completed' && (p.landingMessage || p.llmMessage))
                .map((p) =>
                  progressPointToReportItem({
                    pointId: p.pointId,
                    pointTitle: p.pointTitle,
                    status: p.status,
                    landingMessage: p.landingMessage,
                    llmMessage: p.llmMessage,
                    agreementJson: p.agreementJson as DualVerifyReportItem['agreement'],
                    errorMessage: p.errorMessage,
                  }),
                );
              if (report.length) {
                this.applyReport(report, section, focus);
              } else {
                this.trySeededCompliance(compliance, section, focus);
              }
            },
            error: () => this.trySeededCompliance(compliance, section, focus),
          });
          return;
        }

        this.trySeededCompliance(compliance, section, focus);
      },
      error: () => {
        this.loading = false;
        this.loadError = null;
      },
    });
  }

  private trySeededCompliance(
    sessions: { id: string; label?: string; comparedPoints?: number }[],
    section: string | null,
    focus: string | null,
  ): void {
    const seeded =
      sessions.find((s) => s.id === SEEDED_COMPLIANCE_SESSION) ??
      sessions.find((s) => (s.comparedPoints ?? 0) > 0);

    if (!seeded) {
      this.loading = false;
      this.sessionKey = '';
      this.items = [];
      return;
    }

    this.sessionKey = `compliance:${seeded.id}`;
    this.deletableSessionId = seeded.id;
    this.deletableSessionKind = 'compliance';
    this.sourceLabel = seeded.label || 'I M P T F S.pdf vs. TFS Guidelines';
    this.api.loadComplianceSession(seeded.id).subscribe({
      next: (r) => {
        const report: DualVerifyReportItem[] = [];
        for (const row of (r.results as Record<string, unknown>[]) ?? []) {
          const item = savedResultToReportItem(
            row as Parameters<typeof savedResultToReportItem>[0],
          );
          if (item) report.push(item);
        }
        this.applyReport(report, section, focus);
      },
      error: () => {
        this.loading = false;
        this.items = [];
      },
    });
  }

  private applyReport(
    report: DualVerifyReportItem[],
    section: string | null,
    focus: string | null,
  ): void {
    const overlays = this.sessionKey ? loadGapDrafts(this.sessionKey) : {};
    this.pointIds = report
      .filter(
        (i) =>
          (i.status === 'completed' || i.status === 'loaded' || i.status === 'failed') &&
          (Boolean(i.landingMessage?.trim() && i.llmMessage?.trim()) ||
            Boolean(i.agreement?.summary) ||
            Boolean(i.errorMessage)),
      )
      .map((i) => i.pointId);

    let items = reportItemsToGapItems(report, overlays);

    if (section) {
      items = items.map((i) => ({ ...i, expanded: i.section === section }));
    }
    if (focus) {
      const f = focus.toLowerCase();
      items = items.map((i) => ({
        ...i,
        expanded: i.title.toLowerCase().includes(f) || i.section.toLowerCase().includes(f),
      }));
    }

    this.items = items;
    this.loading = false;
    this.loadError = items.length
      ? null
      : 'No saved findings in this session — the run may have been cancelled, failed, or never finished.';
  }

  private persistSoon(): void {
    if (this.saveTimer) clearTimeout(this.saveTimer);
    this.saveTimer = setTimeout(() => this.persistDrafts(), 400);
  }

  private persistDrafts(): void {
    if (!this.sessionKey || !this.pointIds.length) return;
    const overlays: Record<string, GapDraftOverlay> = {};
    this.items.forEach((item, i) => {
      const pointId = this.pointIds[i];
      if (!pointId) return;
      overlays[pointId] = {
        gaps: item.gaps,
        managementResponse: item.managementResponse,
        designEffectiveness: item.designEffectiveness,
        operatingEffectiveness: item.operatingEffectiveness,
        overallEffectiveness: item.overallEffectiveness,
        documentReference: item.documentReference,
        evidence: item.evidence,
        signedOff: item.signedOff,
        expanded: item.expanded,
      };
    });
    saveGapDrafts(this.sessionKey, overlays);
  }
}
