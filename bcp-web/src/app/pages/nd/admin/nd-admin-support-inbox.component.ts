import { Component, ElementRef, OnDestroy, OnInit, ViewChild, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { NdApiService } from '../../../services/nd/nd-api.service';
import { NdAuthService } from '../../../services/nd/nd-auth.service';
import { ToastService } from '../../../services/toast.service';
import { formatNdDateTime } from '../../../../lib/nd/date-format';
import {
  SUPPORT_CATEGORIES,
  SUPPORT_PRIORITIES,
  SUPPORT_STATUSES,
  supportCategoryLabel,
  supportPriorityBadge,
  supportPriorityLabel,
  supportStatusBadge,
  supportStatusLabel,
  type SupportMessage,
  type SupportTicket,
} from '../../../../lib/nd/support';

const THREAD_POLL_MS = 8_000;
const LIST_POLL_MS = 30_000;

/** Platform super admin: every workspace's support tickets, with the chat, internal notes and status controls. */
@Component({
  selector: 'app-nd-admin-support-inbox',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './nd-admin-support-inbox.component.html',
  styleUrls: ['../nd-shared.scss', './nd-admin-support-inbox.component.scss'],
})
export class NdAdminSupportInboxComponent implements OnInit, OnDestroy {
  private readonly api = inject(NdApiService);
  private readonly auth = inject(NdAuthService);
  private readonly toast = inject(ToastService);

  @ViewChild('messagesEnd') private messagesEnd?: ElementRef<HTMLElement>;

  readonly categories = SUPPORT_CATEGORIES;
  readonly priorities = SUPPORT_PRIORITIES;
  readonly statuses = SUPPORT_STATUSES;
  readonly categoryLabel = supportCategoryLabel;
  readonly priorityLabel = supportPriorityLabel;
  readonly priorityBadge = supportPriorityBadge;
  readonly statusBadge = supportStatusBadge;
  readonly statusLabel = (s: string) => supportStatusLabel(s);

  // filters
  status = 'active';
  workspaceId = '';
  category = '';
  priority = '';
  search = '';
  workspaces: { id: string; name: string }[] = [];

  tickets: SupportTicket[] = [];
  loadingList = true;
  listError = '';

  selected: SupportTicket | null = null;
  messages: SupportMessage[] = [];
  loadingThread = false;
  reply = '';
  internal = false;
  sending = false;
  saving = false;

  private listTimer: ReturnType<typeof setInterval> | null = null;
  private threadTimer: ReturnType<typeof setInterval> | null = null;
  private searchTimer: ReturnType<typeof setTimeout> | null = null;

  get unreadCount(): number {
    return this.tickets.filter((t) => t.unread).length;
  }

  ngOnInit(): void {
    void this.loadWorkspaces();
    void this.loadList();
    this.listTimer = setInterval(() => {
      if (!document.hidden) void this.loadList(true);
    }, LIST_POLL_MS);
  }

  ngOnDestroy(): void {
    if (this.listTimer) clearInterval(this.listTimer);
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.stopThreadPoll();
  }

  formatWhen(iso: string | null | undefined): string {
    return formatNdDateTime(iso, this.auth.profile()?.workspace?.dateFormatRegion);
  }

  private async loadWorkspaces(): Promise<void> {
    const res = await this.api.getSupportInboxWorkspaces();
    if (res.success) this.workspaces = res.data ?? [];
  }

  async loadList(silent = false): Promise<void> {
    if (!silent) this.loadingList = true;
    const res = await this.api.getSupportInbox({
      status: this.status,
      workspaceId: this.workspaceId,
      category: this.category,
      priority: this.priority,
      q: this.search.trim(),
    });
    this.loadingList = false;
    if (res.success) {
      this.tickets = res.data ?? [];
      this.listError = '';
      // Keep the open ticket's row in sync (status, unread) without reloading the thread.
      if (this.selected) {
        const row = this.tickets.find((t) => t.id === this.selected!.id);
        if (row) row.unread = false;
      }
    } else if (!silent) {
      this.listError = res.message || 'Could not load support tickets.';
    }
  }

  onFilterChange(): void {
    void this.loadList();
  }

  onSearchInput(): void {
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.searchTimer = setTimeout(() => void this.loadList(), 300);
  }

  clearFilters(): void {
    this.status = 'active';
    this.workspaceId = '';
    this.category = '';
    this.priority = '';
    this.search = '';
    void this.loadList();
  }

  async select(t: SupportTicket): Promise<void> {
    this.selected = t;
    this.messages = [];
    this.reply = '';
    this.internal = false;
    this.loadingThread = true;
    await this.refreshThread();
    this.loadingThread = false;
    t.unread = false;
    this.stopThreadPoll();
    this.threadTimer = setInterval(() => {
      if (!document.hidden) void this.refreshThread(true);
    }, THREAD_POLL_MS);
  }

  private async refreshThread(silent = false): Promise<void> {
    const id = this.selected?.id;
    if (!id) return;
    const res = await this.api.getSupportInboxThread(id);
    if (this.selected?.id !== id) return;
    if (!res.success || !res.data) {
      if (!silent) this.toast.show(res.message || 'Could not load this ticket.', 'error');
      return;
    }
    const grew = res.data.messages.length !== this.messages.length;
    this.selected = res.data.ticket;
    this.messages = res.data.messages;
    this.patchRow(res.data.ticket);
    if (grew) this.scrollToEnd();
  }

  async sendReply(): Promise<void> {
    const text = this.reply.trim();
    if (!text || !this.selected || this.sending) return;
    this.sending = true;
    const res = await this.api.replySupportInbox(this.selected.id, text, this.internal);
    this.sending = false;
    if (!res.success || !res.data) {
      this.toast.show(res.message || 'Could not send.', 'error');
      return;
    }
    this.reply = '';
    this.messages = [...this.messages, res.data];
    if (!this.internal && this.selected.status === 'open') {
      this.selected = { ...this.selected, status: 'waiting_on_client', lastMessageAt: res.data.createdAt };
      this.patchRow(this.selected);
    }
    this.scrollToEnd();
  }

  onReplyKey(e: KeyboardEvent): void {
    if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
      e.preventDefault();
      void this.sendReply();
    }
  }

  async update(field: 'status' | 'priority', value: string): Promise<void> {
    if (!this.selected || this.saving) return;
    this.saving = true;
    const res = await this.api.updateSupportInboxTicket(this.selected.id, { [field]: value });
    this.saving = false;
    if (!res.success || !res.data) {
      this.toast.show(res.message || 'Could not update the ticket.', 'error');
      return;
    }
    this.selected = res.data;
    this.patchRow(res.data);
    this.toast.show(field === 'status' ? `Status set to ${this.statusLabel(value)}.` : `Priority set to ${this.priorityLabel(value)}.`, 'success');
  }

  private patchRow(t: SupportTicket): void {
    const i = this.tickets.findIndex((r) => r.id === t.id);
    if (i >= 0) this.tickets[i] = { ...this.tickets[i], ...t, unread: false };
  }

  private stopThreadPoll(): void {
    if (this.threadTimer) clearInterval(this.threadTimer);
    this.threadTimer = null;
  }

  private scrollToEnd(): void {
    setTimeout(() => this.messagesEnd?.nativeElement.scrollIntoView({ block: 'end' }), 0);
  }
}
