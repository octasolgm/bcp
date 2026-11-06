import { Component, ElementRef, EventEmitter, Input, OnDestroy, OnInit, Output, ViewChild, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { NdApiService } from '../../../services/nd/nd-api.service';
import { NdAuthService } from '../../../services/nd/nd-auth.service';
import { ToastService } from '../../../services/toast.service';
import { formatNdDateTime } from '../../../../lib/nd/date-format';
import {
  SUPPORT_CATEGORIES,
  SUPPORT_PRIORITIES,
  supportCategoryLabel,
  supportPriorityBadge,
  supportPriorityLabel,
  supportStatusBadge,
  supportStatusLabel,
  type SupportCategory,
  type SupportMessage,
  type SupportPriority,
  type SupportTicket,
} from '../../../../lib/nd/support';

type View = 'list' | 'new' | 'thread';

/** How often an open chat checks for new replies, and how often the ticket list refreshes. */
const THREAD_POLL_MS = 8_000;
const LIST_POLL_MS = 30_000;

/**
 * Contact support for a workspace user: their tickets, a form for a new request, and the chat on one ticket.
 * Used inside the floating Support panel and on the full Support page.
 */
@Component({
  selector: 'app-nd-support-center',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './nd-support-center.component.html',
  styleUrls: ['../../../pages/nd/nd-shared.scss', './nd-support-center.component.scss'],
})
export class NdSupportCenterComponent implements OnInit, OnDestroy {
  private readonly api = inject(NdApiService);
  private readonly auth = inject(NdAuthService);
  private readonly toast = inject(ToastService);

  /** 'panel' = compact, inside the floating drawer; 'page' = the full Support page. */
  @Input() mode: 'panel' | 'page' = 'panel';
  /** Fired whenever the unread count may have changed, so the Support button can refresh its badge. */
  @Output() unreadChanged = new EventEmitter<void>();

  @ViewChild('messagesEnd') private messagesEnd?: ElementRef<HTMLElement>;

  readonly categories = SUPPORT_CATEGORIES;
  readonly priorities = SUPPORT_PRIORITIES;
  readonly categoryLabel = supportCategoryLabel;
  readonly priorityLabel = supportPriorityLabel;
  readonly priorityBadge = supportPriorityBadge;
  readonly statusBadge = supportStatusBadge;
  statusLabel = (s: string) => supportStatusLabel(s, true);

  view: View = 'list';
  tickets: SupportTicket[] = [];
  loadingList = true;
  listError = '';
  filter: 'active' | 'all' = 'active';

  // new request form
  subject = '';
  category: SupportCategory = 'issue';
  priority: SupportPriority = 'normal';
  message = '';
  creating = false;

  // thread
  ticket: SupportTicket | null = null;
  messages: SupportMessage[] = [];
  loadingThread = false;
  reply = '';
  sending = false;
  changingStatus = false;

  private listTimer: ReturnType<typeof setInterval> | null = null;
  private threadTimer: ReturnType<typeof setInterval> | null = null;

  get isWorkspaceAdmin(): boolean {
    return this.auth.getRole() === 'super_admin';
  }

  get visibleTickets(): SupportTicket[] {
    if (this.filter === 'all') return this.tickets;
    return this.tickets.filter((t) => t.status === 'open' || t.status === 'waiting_on_client');
  }

  ngOnInit(): void {
    void this.loadList();
    this.listTimer = setInterval(() => {
      if (this.view === 'list') void this.loadList(true);
    }, LIST_POLL_MS);
  }

  ngOnDestroy(): void {
    if (this.listTimer) clearInterval(this.listTimer);
    this.stopThreadPoll();
  }

  formatWhen(iso: string | null | undefined): string {
    return formatNdDateTime(iso, this.auth.profile()?.workspace?.dateFormatRegion);
  }

  async loadList(silent = false): Promise<void> {
    if (!silent) this.loadingList = true;
    const res = await this.api.getSupportTickets();
    this.loadingList = false;
    if (res.success) {
      this.tickets = res.data ?? [];
      this.listError = '';
    } else if (!silent) {
      this.listError = res.message || 'Could not load your support requests.';
    }
  }

  openNew(): void {
    this.stopThreadPoll();
    this.view = 'new';
    this.subject = '';
    this.message = '';
    this.category = 'issue';
    this.priority = 'normal';
  }

  backToList(): void {
    this.stopThreadPoll();
    this.view = 'list';
    this.ticket = null;
    this.messages = [];
    void this.loadList(true);
  }

  async submitNew(): Promise<void> {
    if (this.creating) return;
    const subject = this.subject.trim();
    const message = this.message.trim();
    if (subject.length < 3) {
      this.toast.show('Please add a short subject (at least 3 characters).', 'warning');
      return;
    }
    if (!message) {
      this.toast.show('Please describe your request.', 'warning');
      return;
    }
    this.creating = true;
    const res = await this.api.createSupportTicket({ subject, message, category: this.category, priority: this.priority });
    this.creating = false;
    if (!res.success || !res.data) {
      this.toast.show(res.message || 'Could not send your request.', 'error');
      return;
    }
    this.toast.show(`Request ${res.data.number} sent. Our team will reply here.`, 'success');
    await this.openTicket(res.data);
  }

  async openTicket(t: SupportTicket): Promise<void> {
    this.view = 'thread';
    this.ticket = t;
    this.messages = [];
    this.reply = '';
    this.loadingThread = true;
    await this.refreshThread();
    this.loadingThread = false;
    this.stopThreadPoll();
    this.threadTimer = setInterval(() => void this.refreshThread(true), THREAD_POLL_MS);
  }

  private async refreshThread(silent = false): Promise<void> {
    const id = this.ticket?.id;
    if (!id) return;
    const res = await this.api.getSupportThread(id);
    if (this.ticket?.id !== id) return;
    if (!res.success || !res.data) {
      if (!silent) this.toast.show(res.message || 'Could not load this request.', 'error');
      return;
    }
    const grew = res.data.messages.length !== this.messages.length;
    const wasUnread = this.ticket?.unread;
    this.ticket = res.data.ticket;
    this.messages = res.data.messages;
    if (grew) this.scrollToEnd();
    if (wasUnread || grew) this.unreadChanged.emit();
  }

  async sendReply(): Promise<void> {
    const text = this.reply.trim();
    if (!text || !this.ticket || this.sending) return;
    this.sending = true;
    const res = await this.api.replySupportTicket(this.ticket.id, text);
    this.sending = false;
    if (!res.success || !res.data) {
      this.toast.show(res.message || 'Could not send your message.', 'error');
      return;
    }
    this.reply = '';
    this.messages = [...this.messages, res.data];
    this.ticket = { ...this.ticket, status: 'open' };
    this.scrollToEnd();
  }

  onReplyKey(e: KeyboardEvent): void {
    if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
      e.preventDefault();
      void this.sendReply();
    }
  }

  async setStatus(status: 'resolved' | 'open'): Promise<void> {
    if (!this.ticket || this.changingStatus) return;
    this.changingStatus = true;
    const res = await this.api.setSupportTicketStatus(this.ticket.id, status);
    this.changingStatus = false;
    if (!res.success || !res.data) {
      this.toast.show(res.message || 'Could not update this request.', 'error');
      return;
    }
    this.ticket = res.data;
    this.toast.show(status === 'resolved' ? 'Marked as resolved.' : 'Request reopened.', 'success');
  }

  isMine(t: SupportTicket): boolean {
    return t.createdBy === this.auth.profile()?.id;
  }

  private stopThreadPoll(): void {
    if (this.threadTimer) clearInterval(this.threadTimer);
    this.threadTimer = null;
  }

  private scrollToEnd(): void {
    setTimeout(() => this.messagesEnd?.nativeElement.scrollIntoView({ block: 'end' }), 0);
  }
}
