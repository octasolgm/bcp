import { Component, OnInit, ViewChild, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { NdApiService } from '../../../services/nd/nd-api.service';
import { NdAuthService } from '../../../services/nd/nd-auth.service';
import { NdLibraryPointsPickerComponent } from './nd-library-points-picker.component';
import type { LibraryPointInput, LibrarySummary } from '../../../../lib/nd/types';

type ApiLibraryPoint = {
  regulationPointId: string;
  regulationDocumentId: string;
  displayOrder: number;
  pointSnapshot?: string | Record<string, unknown>;
};

@Component({
  selector: 'app-nd-library-builder',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink, NdLibraryPointsPickerComponent],
  templateUrl: './nd-library-builder.component.html',
  styleUrls: ['./nd-library-builder.component.scss', '../nd-shared.scss'],
})
export class NdLibraryBuilderComponent implements OnInit {
  private readonly api = inject(NdApiService);
  private readonly auth = inject(NdAuthService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  libraryId: string | null = null;
  isNew = true;
  name = '';
  description = '';
  initialPoints: LibraryPointInput[] = [];
  points: LibraryPointInput[] = [];
  pickerRevision = 0;
  loading = true;
  saving = false;
  error = '';
  success = '';

  @ViewChild(NdLibraryPointsPickerComponent)
  private picker?: NdLibraryPointsPickerComponent;

  async ngOnInit(): Promise<void> {
    await this.auth.refreshProfile();
    const id = this.route.snapshot.paramMap.get('libraryId');
    this.isNew = !id || id === 'new';
    this.libraryId = this.isNew ? null : id;

    if (!this.isNew && this.libraryId) {
      this.applyPrefillFromNavigation();
      await this.loadLibrary(this.libraryId);
    } else {
      this.loading = false;
    }
  }

  get pageTitle(): string {
    return this.isNew ? 'New Regulation Points Library' : 'Edit Regulation Points Library';
  }

  get saveLabel(): string {
    if (this.saving) return 'Saving…';
    return this.isNew ? 'Create library' : 'Update library';
  }

  private applyPrefillFromNavigation(): void {
    const state = history.state as { library?: Pick<LibrarySummary, 'name' | 'description'> } | null;
    const lib = state?.library;
    if (!lib) return;
    if (lib.name) this.name = lib.name;
    if (lib.description != null) this.description = lib.description;
  }

  private async loadLibrary(id: string): Promise<void> {
    this.loading = true;
    this.error = '';
    const res = await this.api.getLibrary(id);
    if (res.success && res.data) {
      const data = res.data as Record<string, unknown>;
      const nested = data['library'] as Record<string, unknown> | undefined;
      this.name = this.asText(data['name'] ?? nested?.['name']) || this.name;
      this.description = this.asText(data['description'] ?? nested?.['description']) || this.description;
      const rawPoints = (data['points'] ?? nested?.['points'] ?? []) as ApiLibraryPoint[];
      this.initialPoints = rawPoints.map((p) => ({
        regulationPointId: String(p.regulationPointId),
        regulationDocumentId: String(p.regulationDocumentId),
        displayOrder: p.displayOrder,
        pointSnapshot: this.parseSnapshot(p.pointSnapshot),
      }));
      this.points = [...this.initialPoints];
      this.pickerRevision++;
    } else if (!this.name) {
      this.error = res.message ?? 'Failed to load regulation points library';
    }
    this.loading = false;
  }

  private asText(value: unknown): string {
    if (value == null) return '';
    if (typeof value === 'string') return value;
    return String(value);
  }

  onPointsChange(points: LibraryPointInput[]): void {
    this.points = points;
    this.success = '';
  }

  private buildSaveBody(points: LibraryPointInput[]) {
    const seenIds = new Set<string>();
    return {
      name: this.name.trim(),
      description: this.description.trim() || null,
      points: points.map((p, i) => {
        let regulationPointId = p.regulationPointId;
        if (seenIds.has(regulationPointId)) {
          regulationPointId = crypto.randomUUID();
        }
        seenIds.add(regulationPointId);
        return {
          regulationPointId,
          regulationDocumentId: p.regulationDocumentId,
          displayOrder: i + 1,
          pointSnapshot: p.pointSnapshot ?? {},
        };
      }),
    };
  }

  async handleSave(): Promise<void> {
    const points = this.picker?.getSelectedPoints() ?? this.points;

    if (!this.name.trim()) {
      this.error = 'Name is required';
      return;
    }
    if (points.length === 0) {
      this.error = 'Add at least one regulation point to the regulation points library';
      return;
    }

    this.saving = true;
    this.error = '';
    this.success = '';
    const body = this.buildSaveBody(points);

    const res = this.isNew
      ? await this.api.createLibrary(body)
      : await this.api.updateLibrary(this.libraryId!, body);

    if (res.success) {
      this.points = points;
      if (this.isNew) {
        const id = (res.data as { id: string }).id;
        this.libraryId = id;
        this.isNew = false;
        await this.router.navigate(['/nd/libraries', id], { replaceUrl: true });
      }
      await this.loadLibrary(this.libraryId!);
      this.success = 'Library saved successfully';
    } else {
      this.error = res.message ?? 'Failed to save';
    }
    this.saving = false;
  }

  private parseSnapshot(snapshot: ApiLibraryPoint['pointSnapshot']): Record<string, unknown> | undefined {
    if (!snapshot) return undefined;
    if (typeof snapshot === 'string') {
      try {
        return JSON.parse(snapshot) as Record<string, unknown>;
      } catch {
        return undefined;
      }
    }
    return snapshot;
  }
}
