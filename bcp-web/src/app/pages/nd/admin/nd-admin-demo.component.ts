import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { NdApiService } from '../../../services/nd/nd-api.service';
import { NdAuthService } from '../../../services/nd/nd-auth.service';
import { NdGapPointDetailComponent } from '../../../components/nd/nd-gap-point-detail.component';
import {
  demoTemplatePointToPreview,
  findRegulationPointForClause,
  mapDemoFinalStatus,
} from '../../../../lib/nd/demo-template-point-preview';
import {
  complianceSeverityLabel,
  type ComplianceSeverity,
} from '../../../../lib/nd/point-compliance-status';
import type { AnalysisPoint, PointSnapshot, RegulationPoint } from '../../../../lib/nd/types';

export type DemoTemplateSummary = {
  id: string;
  code: string;
  name: string;
  description?: string | null;
  regulationNameHint: string;
  internalNameHint: string;
  isActive: boolean;
  sortOrder: number;
  pointCount: number;
  updatedAt?: string;
};

export type DemoTemplatePoint = {
  id: string;
  templateId: string;
  clauseNo: string;
  clauseTitle?: string | null;
  designStatus: string;
  operatingStatus: string;
  overallStatus: string;
  confidence: number;
  interpretation: string;
  policyExtract: string[];
  documentReference: string;
  gapDescription: string;
  suggestedAction: string;
  gapDirection: string;
  sortOrder: number;
};

export type DemoTemplateDetail = DemoTemplateSummary & {
  points: DemoTemplatePoint[];
};

@Component({
  selector: 'app-nd-admin-demo',
  standalone: true,
  imports: [CommonModule, FormsModule, NdGapPointDetailComponent],
  templateUrl: './nd-admin-demo.component.html',
  styleUrls: ['./nd-admin-demo.component.scss', '../nd-shared.scss'],
})
export class NdAdminDemoComponent implements OnInit {
  private readonly api = inject(NdApiService);
  readonly auth = inject(NdAuthService);

  loading = true;
  error = '';
  message = '';
  clearing = false;

  clearAll = false;
  clearInternalDocuments = false;
  clearRegulationDocuments = false;
  clearLibraries = false;
  clearAnalysisRuns = false;
  clearUsers = false;

  templates: DemoTemplateSummary[] = [];
  selected: DemoTemplateDetail | null = null;
  selectedLoading = false;
  savingMeta = false;

  previewPointId: string | null = null;
  previewAnalysisPoint: AnalysisPoint | null = null;
  previewSnapshot: PointSnapshot | null = null;
  previewClause: string | null = null;
  private regulationPoints: RegulationPoint[] = [];

  pointEditorMode: 'add' | 'edit' | null = null;
  pointFormSaving = false;
  pointFormDeleting = false;
  pointEditorId: string | null = null;
  pointForm = this.emptyPointForm();

  readonly statusOptions = [
    { value: 'compliant', label: 'Compliant' },
    { value: 'partial', label: 'Partial' },
    { value: 'non_compliant', label: 'Non-compliant' },
  ];

  async ngOnInit(): Promise<void> {
    await this.auth.refreshProfile();
    await this.reload();
  }

  get isSuperAdmin(): boolean {
    return this.auth.getRole() === 'super_admin';
  }

  async reload(): Promise<void> {
    this.loading = true;
    this.error = '';
    try {
      const res = await this.api.getDemoAdminOverview();
      if (!res.success || !res.data) {
        this.error = res.message ?? 'Could not load demo admin';
        return;
      }
      this.templates = (res.data as { templates: DemoTemplateSummary[] }).templates ?? [];
    } finally {
      this.loading = false;
    }
  }

  async clearDemo(): Promise<void> {
    if (!this.isSuperAdmin || this.clearing) return;
    const body = {
      clearAll: this.clearAll,
      clearInternalDocuments: this.clearInternalDocuments,
      clearRegulationDocuments: this.clearRegulationDocuments,
      clearLibraries: this.clearLibraries,
      clearAnalysisRuns: this.clearAnalysisRuns,
      clearUsers: this.clearUsers,
    };
    if (
      !body.clearAll &&
      !body.clearInternalDocuments &&
      !body.clearRegulationDocuments &&
      !body.clearLibraries &&
      !body.clearAnalysisRuns &&
      !body.clearUsers
    ) {
      this.error = 'Select at least one clear option.';
      return;
    }
    const ok = window.confirm(
      'Clear selected demo workspace data? Soft-deleted demo documents and analyses are permanently removed from Deleted. Demo analysis templates (analys1demo / analys2demo) are never deleted.',
    );
    if (!ok) return;

    this.clearing = true;
    this.error = '';
    this.message = '';
    try {
      const res = await this.api.clearDemoWorkspace(body);
      if (!res.success) {
        this.error = res.message ?? 'Clear failed';
        return;
      }
      const d = res.data as {
        internalDocuments: number;
        regulationDocuments: number;
        libraries: number;
        analysisRuns: number;
        usersDeactivated: number;
        deletedAnalysisRunsPurged?: number;
        deletedInternalDocumentsPurged?: number;
        deletedRegulationDocumentsPurged?: number;
      };
      const deletedRunsPurged = d.deletedAnalysisRunsPurged ?? 0;
      const deletedInternalPurged = d.deletedInternalDocumentsPurged ?? 0;
      const deletedRegsPurged = d.deletedRegulationDocumentsPurged ?? 0;
      this.message =
        `Cleared demo data — internal: ${d.internalDocuments}, regulations: ${d.regulationDocuments}, ` +
        `libraries: ${d.libraries}, analyses: ${d.analysisRuns}, ` +
        `deleted purged — internal: ${deletedInternalPurged}, regulations: ${deletedRegsPurged}, analyses: ${deletedRunsPurged}, ` +
        `users deactivated: ${d.usersDeactivated}. Seed templates preserved.`;
      this.clearAll = false;
      this.clearInternalDocuments = false;
      this.clearRegulationDocuments = false;
      this.clearLibraries = false;
      this.clearAnalysisRuns = false;
      this.clearUsers = false;
    } finally {
      this.clearing = false;
    }
  }

  async openTemplate(id: string): Promise<void> {
    this.selectedLoading = true;
    this.error = '';
    this.previewPointId = null;
    this.previewAnalysisPoint = null;
    this.previewSnapshot = null;
    this.regulationPoints = [];
    this.cancelPointEditor();
    try {
      const res = await this.api.getDemoAnalysisTemplate(id);
      if (!res.success || !res.data) {
        this.error = res.message ?? 'Could not load template';
        this.selected = null;
        return;
      }
      this.selected = res.data as DemoTemplateDetail;
      await this.loadRegulationPointsForTemplate(this.selected);
      if (this.selected.points.length > 0) {
        this.selectPreviewPoint(this.selected.points[0]);
      }
    } finally {
      this.selectedLoading = false;
    }
  }

  private async loadRegulationPointsForTemplate(template: DemoTemplateDetail): Promise<void> {
    const hint = template.regulationNameHint?.trim();
    if (!hint) return;
    const docsRes = await this.api.getRegulationDocuments();
    if (!docsRes.success || !docsRes.data) return;
    const docs = docsRes.data as { id: string; name: string; isManual?: boolean }[];
    const doc = docs.find(
      (d) =>
        !d.isManual &&
        (d.name?.toLowerCase().includes(hint.toLowerCase()) ||
          hint.toLowerCase().includes(d.name?.toLowerCase() ?? '')),
    );
    if (!doc) return;
    const pointsRes = await this.api.getDocumentPoints(doc.id);
    if (pointsRes.success && pointsRes.data) {
      this.regulationPoints = pointsRes.data as RegulationPoint[];
    }
  }

  selectPreviewPoint(point: DemoTemplatePoint): void {
    if (this.pointEditorMode) return;
    this.previewPointId = point.id;
    this.previewClause = point.clauseNo;
    const regPoint = findRegulationPointForClause(this.regulationPoints, point.clauseNo);
    const preview = demoTemplatePointToPreview(point, regPoint);
    this.previewAnalysisPoint = preview.analysisPoint;
    this.previewSnapshot = preview.snapshot;
  }

  private emptyPointForm(): {
    clauseNo: string;
    clauseTitle: string;
    designStatus: string;
    operatingStatus: string;
    overallStatus: string;
    confidence: number;
    interpretation: string;
    policyExtractText: string;
    documentReference: string;
    gapDescription: string;
    suggestedAction: string;
    gapDirection: string;
    sortOrder: number;
  } {
    return {
      clauseNo: '',
      clauseTitle: '',
      designStatus: 'partial',
      operatingStatus: 'partial',
      overallStatus: 'partial',
      confidence: 0.85,
      interpretation: '',
      policyExtractText: '',
      documentReference: '',
      gapDescription: '',
      suggestedAction: '',
      gapDirection: 'policy_gap',
      sortOrder: this.selected?.points.length ?? 0,
    };
  }

  private pointFormFromTemplate(point: DemoTemplatePoint): typeof this.pointForm {
    return {
      clauseNo: point.clauseNo ?? '',
      clauseTitle: point.clauseTitle ?? '',
      designStatus: point.designStatus ?? 'partial',
      operatingStatus: point.operatingStatus ?? 'partial',
      overallStatus: point.overallStatus ?? 'partial',
      confidence: point.confidence ?? 0,
      interpretation: point.interpretation ?? '',
      policyExtractText: (point.policyExtract ?? []).join('\n'),
      documentReference: point.documentReference ?? '',
      gapDescription: point.gapDescription ?? '',
      suggestedAction: point.suggestedAction ?? '',
      gapDirection: point.gapDirection ?? '',
      sortOrder: point.sortOrder ?? 0,
    };
  }

  private pointPayloadFromForm(): Record<string, unknown> {
    const lines = this.pointForm.policyExtractText
      .split('\n')
      .map((l) => l.trim())
      .filter(Boolean);
    return {
      clauseNo: this.pointForm.clauseNo.trim(),
      clauseTitle: this.pointForm.clauseTitle.trim(),
      designStatus: this.pointForm.designStatus,
      operatingStatus: this.pointForm.operatingStatus,
      overallStatus: this.pointForm.overallStatus,
      confidence: Number(this.pointForm.confidence),
      interpretation: this.pointForm.interpretation,
      policyExtract: lines,
      documentReference: this.pointForm.documentReference,
      gapDescription: this.pointForm.gapDescription,
      suggestedAction: this.pointForm.suggestedAction,
      gapDirection: this.pointForm.gapDirection,
      sortOrder: Number(this.pointForm.sortOrder),
    };
  }

  startAddPoint(): void {
    if (!this.selected) return;
    this.pointEditorMode = 'add';
    this.pointEditorId = null;
    this.pointForm = this.emptyPointForm();
    this.previewPointId = null;
    this.previewAnalysisPoint = null;
    this.previewSnapshot = null;
  }

  startEditPoint(point: DemoTemplatePoint, event?: Event): void {
    event?.stopPropagation();
    event?.preventDefault();
    if (!this.selected) return;
    this.pointEditorMode = 'edit';
    this.pointEditorId = point.id;
    this.pointForm = this.pointFormFromTemplate(point);
    this.selectPreviewPoint(point);
  }

  cancelPointEditor(): void {
    this.pointEditorMode = null;
    this.pointEditorId = null;
    this.pointFormSaving = false;
    this.pointFormDeleting = false;
    this.pointForm = this.emptyPointForm();
  }

  async savePointEditor(): Promise<void> {
    if (!this.selected || !this.pointEditorMode || this.pointFormSaving) return;
    if (!this.pointForm.clauseNo.trim()) {
      this.error = 'Clause number is required.';
      return;
    }
    this.pointFormSaving = true;
    this.error = '';
    try {
      const payload = this.pointPayloadFromForm();
      const templateId = this.selected.id;
      const res =
        this.pointEditorMode === 'add'
          ? await this.api.addDemoAnalysisTemplatePoint(templateId, payload)
          : await this.api.updateDemoAnalysisTemplatePoint(
              templateId,
              this.pointEditorId!,
              payload,
            );
      if (!res.success || !res.data) {
        this.error = res.message ?? 'Could not save point';
        return;
      }
      const saved = res.data as DemoTemplatePoint;
      if (this.pointEditorMode === 'add') {
        this.selected.points = [...this.selected.points, saved].sort(
          (a, b) => a.sortOrder - b.sortOrder || a.clauseNo.localeCompare(b.clauseNo, undefined, { numeric: true }),
        );
        this.message = `Added clause §${saved.clauseNo}.`;
      } else {
        const idx = this.selected.points.findIndex((p) => p.id === saved.id);
        if (idx >= 0) this.selected.points[idx] = saved;
        this.message = `Updated clause §${saved.clauseNo}.`;
      }
      this.cancelPointEditor();
      this.selectPreviewPoint(saved);
      await this.reload();
    } finally {
      this.pointFormSaving = false;
    }
  }

  async deleteEditingPoint(): Promise<void> {
    if (!this.selected || this.pointEditorMode !== 'edit' || !this.pointEditorId || this.pointFormDeleting) {
      return;
    }
    if (!window.confirm(`Delete clause §${this.pointForm.clauseNo}? This cannot be undone.`)) return;
    this.pointFormDeleting = true;
    this.error = '';
    try {
      const res = await this.api.deleteDemoAnalysisTemplatePoint(
        this.selected.id,
        this.pointEditorId,
      );
      if (!res.success) {
        this.error = res.message ?? 'Could not delete point';
        return;
      }
      this.selected.points = this.selected.points.filter((p) => p.id !== this.pointEditorId);
      this.message = 'Point deleted.';
      this.cancelPointEditor();
      if (this.selected.points.length > 0) {
        this.selectPreviewPoint(this.selected.points[0]);
      } else {
        this.previewPointId = null;
        this.previewAnalysisPoint = null;
        this.previewSnapshot = null;
      }
      await this.reload();
    } finally {
      this.pointFormDeleting = false;
    }
  }

  isPreviewPoint(point: DemoTemplatePoint): boolean {
    return this.previewPointId === point.id;
  }

  previewStatusLabel(point: DemoTemplatePoint): string {
    const sev = this.templatePointSeverity(point);
    return sev ? complianceSeverityLabel(sev) : '—';
  }

  templatePointSeverity(point: DemoTemplatePoint): ComplianceSeverity | null {
    const fs = mapDemoFinalStatus(point.overallStatus, point.designStatus);
    return fs as ComplianceSeverity;
  }

  templatePointGapCount(point: DemoTemplatePoint): number {
    const sev = this.templatePointSeverity(point);
    if (!sev || sev === 'compliant') return 0;
    return 1;
  }

  templatePointPolicySnippet(point: DemoTemplatePoint): string {
    const extracts = (point.policyExtract ?? []).filter((s) => s?.trim());
    if (extracts.length === 1) {
      const t = extracts[0].trim();
      return t.length > 140 ? `${t.slice(0, 140)}…` : t;
    }
    if (extracts.length > 1) {
      const joined = extracts.map((s, i) => `(${i + 1}) ${s.trim()}`).join(' ');
      return joined.length > 140 ? `${joined.slice(0, 140)}…` : joined;
    }
    return '—';
  }

  formatTemplateConfidence(point: DemoTemplatePoint): string {
    const n = point.confidence;
    if (n == null || Number.isNaN(n)) return '—';
    return `${Math.round(Math.max(0, Math.min(1, n)) * 100)}%`;
  }

  async saveTemplateMeta(): Promise<void> {
    if (!this.selected) return;
    this.savingMeta = true;
    this.error = '';
    try {
      const res = await this.api.updateDemoAnalysisTemplate(this.selected.id, {
        name: this.selected.name,
        description: this.selected.description ?? '',
        regulationNameHint: this.selected.regulationNameHint,
        internalNameHint: this.selected.internalNameHint,
        isActive: this.selected.isActive,
      });
      if (!res.success) {
        this.error = res.message ?? 'Could not save template';
        return;
      }
      this.message = 'Template settings saved.';
      await this.reload();
    } finally {
      this.savingMeta = false;
    }
  }

  async reloadAnalys1FromSeed(): Promise<void> {
    if (!this.selected || this.selected.code !== 'analys1demo') return;
    if (
      !window.confirm(
        'Replace all analys1demo points from SeedData/cbuae-aml-demo-judgments.json? Unsaved edits will be lost.',
      )
    ) {
      return;
    }
    const res = await this.api.reloadDemoAnalysisTemplateFromSeed(this.selected.id);
    if (!res.success) {
      this.error = res.message ?? 'Reload failed';
      return;
    }
    this.message = res.message ?? 'Reloaded from seed file.';
    await this.openTemplate(this.selected.id);
    await this.reload();
  }
}
