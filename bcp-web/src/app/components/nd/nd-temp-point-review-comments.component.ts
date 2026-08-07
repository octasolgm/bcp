import {
  ChangeDetectionStrategy,
  ChangeDetectorRef,
  Component,
  EventEmitter,
  Input,
  OnChanges,
  Output,
  SimpleChanges,
  inject,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { NdApiService } from '../../services/nd/nd-api.service';
import { NdAuthService } from '../../services/nd/nd-auth.service';
import { startPanelResize } from '../../pages/shared/panel-resize';
import { formatDate } from '../../../lib/nd/utils';
import {
  parseTempPointReviewComment,
  type TempPointReviewComment,
  type TempReviewCommentsChangeEvent,
} from '../../../lib/nd/temp-point-review-comment';

@Component({
  selector: 'app-nd-temp-point-review-comments',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './nd-temp-point-review-comments.component.html',
  styleUrl: './nd-temp-point-review-comments.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class NdTempPointReviewCommentsComponent implements OnChanges {
  private static readonly PANEL_WIDTH_KEY = 'nd-review-panel-width';

  private readonly ndApi = inject(NdApiService);
  private readonly ndAuth = inject(NdAuthService);
  private readonly cdr = inject(ChangeDetectorRef);

  @Input({ required: true }) runId!: string;
  @Input({ required: true }) analysisPointId!: string;
  @Input() comments: TempPointReviewComment[] = [];
  @Input() canEdit = true;
  @Output() changed = new EventEmitter<TempReviewCommentsChangeEvent>();

  displayComments: TempPointReviewComment[] = [];
  draft = '';
  saving = false;
  deletingId: string | null = null;
  editingId: string | null = null;
  editDraft = '';
  error = '';
  panelOpen = false;
  panelWidth = 384;

  readonly formatDate = formatDate;

  constructor() {
    this.panelWidth = this.loadPanelWidth();
  }

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['comments']) {
      this.displayComments = [...(this.comments ?? [])];
    }
  }

  openPanel(): void {
    this.panelOpen = true;
    this.cdr.markForCheck();
  }

  closePanel(): void {
    this.panelOpen = false;
    this.cdr.markForCheck();
  }

  startPanelWidthResize(event: MouseEvent): void {
    startPanelResize(
      {
        kind: 'review-panel-width',
        startX: event.clientX,
        startY: event.clientY,
        startVal: this.panelWidth,
      },
      event,
      (_kind, value) => {
        this.panelWidth = value;
        this.savePanelWidth();
        this.cdr.markForCheck();
      },
    );
  }

  private loadPanelWidth(): number {
    try {
      const raw = localStorage.getItem(NdTempPointReviewCommentsComponent.PANEL_WIDTH_KEY);
      const n = raw ? Number(raw) : NaN;
      if (Number.isFinite(n)) return Math.min(560, Math.max(280, n));
    } catch {
      /* ignore */
    }
    return 384;
  }

  private savePanelWidth(): void {
    try {
      localStorage.setItem(
        NdTempPointReviewCommentsComponent.PANEL_WIDTH_KEY,
        String(this.panelWidth),
      );
    } catch {
      /* ignore */
    }
  }

  get currentUserId(): string | null {
    return this.ndAuth.profile()?.id ?? null;
  }

  get isSuperAdmin(): boolean {
    return this.ndAuth.getRole() === 'super_admin';
  }

  canDelete(comment: TempPointReviewComment): boolean {
    if (!this.canEdit) return false;
    if (this.isSuperAdmin) return true;
    return Boolean(this.currentUserId && comment.commentedBy === this.currentUserId);
  }

  canEditComment(comment: TempPointReviewComment): boolean {
    return this.canDelete(comment);
  }

  startEdit(comment: TempPointReviewComment): void {
    this.editingId = comment.id;
    this.editDraft = comment.comment;
    this.error = '';
    this.cdr.markForCheck();
  }

  cancelEdit(): void {
    this.editingId = null;
    this.editDraft = '';
    this.cdr.markForCheck();
  }

  async saveEdit(commentId: string): Promise<void> {
    const text = this.editDraft.trim();
    if (!text || this.saving) return;
    this.saving = true;
    this.error = '';
    const res = await this.ndApi.updateTempPointReviewComment(
      this.runId,
      this.analysisPointId,
      commentId,
      text,
    );
    this.saving = false;
    if (res.success) {
      const updated = parseTempPointReviewComment(res.data);
      if (updated) {
        this.displayComments = this.displayComments.map((c) => (c.id === commentId ? updated : c));
      }
      this.editingId = null;
      this.editDraft = '';
      this.emitChange();
    } else {
      this.error = res.message ?? 'Could not update comment';
    }
    this.cdr.markForCheck();
  }

  async addComment(): Promise<void> {
    const text = this.draft.trim();
    if (!text || this.saving || !this.canEdit) return;
    this.saving = true;
    this.error = '';
    const res = await this.ndApi.addTempPointReviewComment(this.runId, this.analysisPointId, text);
    this.saving = false;
    if (res.success) {
      const created = parseTempPointReviewComment(res.data);
      if (created) {
        this.displayComments = [...this.displayComments, created];
      }
      this.draft = '';
      this.emitChange();
    } else {
      this.error = res.message ?? 'Could not save comment';
    }
    this.cdr.markForCheck();
  }

  async deleteComment(commentId: string): Promise<void> {
    if (this.deletingId || !this.canEdit) return;
    this.deletingId = commentId;
    this.error = '';
    const res = await this.ndApi.deleteTempPointReviewComment(this.runId, this.analysisPointId, commentId);
    this.deletingId = null;
    if (res.success) {
      this.displayComments = this.displayComments.filter((c) => c.id !== commentId);
      this.emitChange();
    } else {
      this.error = res.message ?? 'Could not delete comment';
    }
    this.cdr.markForCheck();
  }

  private emitChange(): void {
    this.changed.emit({
      analysisPointId: this.analysisPointId,
      comments: [...this.displayComments],
    });
  }
}
