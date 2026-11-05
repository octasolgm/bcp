import { Component, EventEmitter, Input, OnChanges, Output, inject } from '@angular/core';
import { NdApiService } from '../../services/nd/nd-api.service';
import { NdAuthService } from '../../services/nd/nd-auth.service';
import { ToastService } from '../../services/toast.service';
import { NdDepartmentPickerComponent, type NdPickerDepartment } from './nd-department-picker.component';

/**
 * "Visible to" control for one analysis, for workspace admins on real accounts. No department picked means all
 * departments. Before the analysis exists (no runId) the choice is only reported through pendingChange.
 */
@Component({
  selector: 'app-nd-run-departments',
  standalone: true,
  imports: [NdDepartmentPickerComponent],
  template: `
    @if (canManage) {
      <span class="rd-wrap" (click)="$event.stopPropagation()">
        <button type="button" class="rd-btn" title="Choose which departments can see this analysis" (click)="openPicker()">
          <span class="rd-label">Visible to</span> {{ label }}
        </button>
        @if (open && departmentList) {
          <app-nd-department-picker
            [departments]="departmentList"
            [selected]="ids"
            title="Visible to departments"
            [subtitle]="runName"
            hint="Leave empty to share with all departments. Picking a department includes its units and teams. Admins and the person who created the analysis always see it."
            emptyLabel="All departments"
            [saving]="saving"
            (saved)="save($event)"
            (closed)="open = false"
          />
        }
      </span>
    }
  `,
  styles: `
    .rd-btn {
      padding: 0.2rem 0.55rem; border: 1px solid var(--border); border-radius: 999px; background: none;
      color: inherit; font: inherit; font-size: 0.75rem; cursor: pointer; white-space: nowrap;
    }
    .rd-btn:hover { border-color: var(--accent); }
    .rd-label { color: var(--text-muted); }
  `,
})
export class NdRunDepartmentsComponent implements OnChanges {
  private readonly api = inject(NdApiService);
  private readonly auth = inject(NdAuthService);
  private readonly toast = inject(ToastService);

  @Input() runId: string | null = null;
  @Input() runName = '';
  /** Lists pass these in so each row does not load them again. */
  @Input() departments: NdPickerDepartment[] | null = null;
  @Input() selectedIds: string[] | null = null;
  @Output() pendingChange = new EventEmitter<string[]>();
  @Output() changed = new EventEmitter<string[]>();

  ids: string[] = [];
  departmentList: NdPickerDepartment[] | null = null;
  open = false;
  saving = false;
  private loadedRunId: string | null = null;

  get canManage(): boolean {
    return this.auth.getRole() === 'super_admin' && !this.auth.isDemoViewer();
  }

  get label(): string {
    if (!this.ids.length) return 'All departments';
    const first = this.departmentList?.find((d) => d.id === this.ids[0])?.name;
    if (!first) return `${this.ids.length} department${this.ids.length === 1 ? '' : 's'}`;
    return this.ids.length > 1 ? `${first} +${this.ids.length - 1}` : first;
  }

  ngOnChanges(): void {
    void this.refresh();
  }

  private async refresh(): Promise<void> {
    if (!this.auth.getRole()) await this.auth.refreshProfile();
    if (!this.canManage) return;
    if (this.departments) this.departmentList = this.departments;
    else if (!this.departmentList) {
      const res = await this.api.getDepartments();
      if (res.success && res.data) this.departmentList = res.data as NdPickerDepartment[];
    }
    if (this.selectedIds) {
      this.ids = this.selectedIds;
    } else if (this.runId && this.runId !== this.loadedRunId) {
      this.loadedRunId = this.runId;
      const res = await this.api.getAnalysisRunDepartments(this.runId);
      if (res.success && res.data) this.ids = res.data;
    }
  }

  async openPicker(): Promise<void> {
    await this.refresh();
    this.open = true;
  }

  async save(ids: string[]): Promise<void> {
    if (!this.runId) {
      this.ids = ids;
      this.open = false;
      this.pendingChange.emit(ids);
      return;
    }
    this.saving = true;
    const res = await this.api.setAnalysisRunDepartments(this.runId, ids);
    this.saving = false;
    if (!res.success) {
      this.toast.show(res.message ?? 'Could not save who can see this analysis', 'error', 4000);
      return;
    }
    this.ids = ids;
    this.open = false;
    this.changed.emit(ids);
    this.toast.show(ids.length ? 'Analysis limited to the selected departments' : 'Analysis shared with all departments', 'success', 3000);
  }
}
