import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { NdStatusBadgeComponent } from '../../../components/nd/nd-status-badge.component';
import { NdApiService } from '../../../services/nd/nd-api.service';
import { NdAuthService } from '../../../services/nd/nd-auth.service';
import type { RegulationDocument, RegulationPoint } from '../../../../lib/nd/types';
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
  extracting = false;
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

  async load(): Promise<void> {
    this.loading = true;
    this.error = '';
    const [docRes, ptsRes] = await Promise.all([
      this.api.getRegulationDocument(this.docId),
      this.api.getDocumentPoints(this.docId),
    ]);
    if (docRes.success && docRes.data) this.doc = docRes.data as RegulationDocument;
    else this.error = docRes.message ?? 'Failed to load document';

    if (ptsRes.success && ptsRes.data) {
      this.points = ptsRes.data as RegulationPoint[];
      this.pointsSource = (ptsRes as { source?: string }).source ?? '';
      const apiCount = (ptsRes as { pointCount?: number }).pointCount;
      const count = apiCount ?? this.points.length;
      if (this.doc) this.doc = { ...this.doc, pointCount: count };
    }
    this.loading = false;
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
}
