import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { NdStatusBadgeComponent } from '../../../components/nd/nd-status-badge.component';
import { NdApiService } from '../../../services/nd/nd-api.service';
import { NdAuthService } from '../../../services/nd/nd-auth.service';
import type { RegulationDocument, RegulationPoint } from '../../../../lib/nd/types';
import { prepareRegulationPointsResponse } from '../../../../lib/regulation-catalog-utils';
import { NdRegulationPointsPanelComponent } from './nd-regulation-points-panel.component';
@Component({
  selector: 'app-nd-regulation-document-detail',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    RouterLink,
    NdStatusBadgeComponent,
    NdRegulationPointsPanelComponent,
  ],
  templateUrl: './nd-regulation-document-detail.component.html',
  styleUrls: ['./nd-regulation-document-detail.component.scss', '../nd-shared.scss'],
})
export class NdRegulationDocumentDetailComponent implements OnInit {
  private readonly api = inject(NdApiService);
  private readonly route = inject(ActivatedRoute);
  readonly auth = inject(NdAuthService);

  docId = '';
  doc: RegulationDocument | null = null;
  points: RegulationPoint[] = [];
  loading = true;
  parsing = false;
  extracting = false;
  repairing = false;
  refreshingPages = false;
  message = '';
  error = '';
  pointsSource = '';

  async ngOnInit(): Promise<void> {
    await this.auth.refreshProfile();
    this.docId = this.route.snapshot.paramMap.get('docId') ?? '';
    await this.load();
  }

  get canExtract(): boolean {
    const role = this.auth.getRole();
    return role === 'maker' || role === 'super_admin';
  }

  get isDemoViewer(): boolean {
    return this.auth.isDemoViewer();
  }

  /** Production makers/admins, or Demo Admin only — not regular demo makers. */
  get showRepair(): boolean {
    if (!this.canExtract || !this.hasPoints) return false;
    if (this.isDemoViewer && !this.auth.isDemoAdmin()) return false;
    return true;
  }

  async load(): Promise<void> {
    this.loading = true;
    this.error = '';
    const [docRes, ptsRes] = await Promise.all([
      this.api.getRegulationDocument(this.docId),
      this.api.getDocumentPoints(this.docId, { lite: true }),
    ]);
    if (docRes.success && docRes.data) this.doc = docRes.data as RegulationDocument;
    else this.error = docRes.message ?? 'Failed to load document';

    if (ptsRes.success && ptsRes.data) {
      const prepared = prepareRegulationPointsResponse(ptsRes.data as unknown[], {
        docName: this.doc?.name,
        apiPointCount: ptsRes.pointCount ?? this.doc?.pointCount,
      });
      this.points = prepared.points;
      this.pointsSource = ptsRes.source ?? '';
    }    this.loading = false;
  }

  get hasPoints(): boolean {
    if (!this.doc) return false;
    const st = (this.doc.extractionStatus ?? '').toLowerCase();
    return st === 'extracted' || st === 'completed' || (this.doc.pointCount ?? 0) > 0 || this.points.length > 0;
  }

  isParsedDoc(): boolean {
    return (this.doc?.extractionStatus ?? '').toLowerCase() === 'parsed';
  }

  needsParse(): boolean {
    if (!this.doc) return false;
    const st = (this.doc.extractionStatus ?? '').toLowerCase();
    return !this.isParsedDoc() && !this.hasPoints && (st === 'pending' || st === 'failed');
  }

  canShowExtract(): boolean {
    if (!this.doc) return false;
    if (this.isDemoViewer) {
      const st = (this.doc.extractionStatus ?? '').toLowerCase();
      return this.isParsedDoc() || this.hasPoints || st === 'pending' || st === 'failed';
    }
    return this.isParsedDoc() || this.hasPoints;
  }

  async handleParse(): Promise<void> {
    if (!this.docId) return;
    this.parsing = true;
    this.error = '';
    this.message = '';
    const res = await this.api.parseRegulationDocument(this.docId);
    if (res.success) {
      this.message = 'Parse complete.';
      await this.load();
    } else {
      this.error = res.message ?? 'Parse failed';
    }
    this.parsing = false;
  }

  async handleRepair(): Promise<void> {
    if (!this.docId) return;
    this.repairing = true;
    this.error = '';
    this.message = '';
    const res = await this.api.repairRegulationPoints(this.docId);
    if (res.success && res.data) {
      const r = res.data.repair;
      if (this.doc && res.data.pointCount != null) {
        this.doc = { ...this.doc, pointCount: res.data.pointCount };
      }
      this.message =
        (r as { recovered?: number }).recovered
          ? `Repaired: ${r.beforeCount} → ${r.afterCount} active (${r.softDeleted} removed, ${(r as { recovered?: number }).recovered} recovered).`
          : `Repaired: ${r.beforeCount} → ${r.afterCount} active (${r.softDeleted} soft-deleted).`;
      await this.load();
    } else {
      this.error = res.message ?? 'Could not repair points';
    }
    this.repairing = false;
  }

  async handleRefreshPages(): Promise<void> {
    if (!this.docId) return;
    this.refreshingPages = true;
    this.error = '';
    this.message = '';
    const res = await this.api.refreshRegulationPageReferences(this.docId);
    if (res.success) {
      this.message = `Updated PDF pages for ${res.data?.pointsUpdated ?? 0} points.`;
      await this.load();
    } else {
      this.error = res.message ?? 'Could not refresh page numbers';
    }
    this.refreshingPages = false;
  }

  async handleExtract(): Promise<void> {
    if (!this.docId) return;
    this.extracting = true;
    this.error = '';
    const res = await this.api.extractRegulationDocument(this.docId);
    if (res.success) {
      await this.load();
    } else {
      this.error = res.message ?? 'Extraction failed';
    }
    this.extracting = false;
  }

  async openRegulationSourcePage(page: number): Promise<void> {
    if (!this.doc) return;
    const fileDocId = this.doc.storedDocumentId ?? this.doc.id;
    const ok = await this.api.openRegulationDocumentPdf(fileDocId, page);
    if (!ok) this.error = 'Could not open document PDF';
  }
}
