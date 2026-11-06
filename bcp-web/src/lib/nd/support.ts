/** Support center: tickets a workspace sends to the platform team, and the chat on each one. */

export type SupportCategory = 'issue' | 'question' | 'knowledge' | 'feature';
export type SupportPriority = 'low' | 'normal' | 'high' | 'urgent';
export type SupportStatus = 'open' | 'waiting_on_client' | 'resolved' | 'closed';

export type SupportTicket = {
  id: string;
  number: string;
  subject: string;
  category: SupportCategory;
  priority: SupportPriority;
  status: SupportStatus;
  createdBy?: string | null;
  createdByName?: string | null;
  createdByEmail?: string | null;
  createdAt: string;
  updatedAt: string;
  lastMessageAt: string;
  unread: boolean;
  workspaceId?: string | null;
  workspaceName?: string | null;
};

export type SupportMessage = {
  id: string;
  body: string;
  isStaff: boolean;
  isInternal: boolean;
  authorName?: string | null;
  createdAt: string;
};

export type SupportThread = { ticket: SupportTicket; messages: SupportMessage[] };

export type NewSupportTicket = {
  subject: string;
  category: SupportCategory;
  priority: SupportPriority;
  message: string;
};

export const SUPPORT_CATEGORIES: { value: SupportCategory; label: string; hint: string }[] = [
  { value: 'issue', label: 'Report an issue', hint: 'Something is not working as expected' },
  { value: 'question', label: 'Ask a question', hint: 'How something works, or what to do next' },
  { value: 'knowledge', label: 'Knowledge request', hint: 'A guide, explanation or how-to you would like' },
  { value: 'feature', label: 'Feature request', hint: 'An idea that would make the platform better' },
];

export const SUPPORT_PRIORITIES: { value: SupportPriority; label: string }[] = [
  { value: 'low', label: 'Low' },
  { value: 'normal', label: 'Normal' },
  { value: 'high', label: 'High' },
  { value: 'urgent', label: 'Urgent' },
];

export const SUPPORT_STATUSES: { value: SupportStatus; label: string }[] = [
  { value: 'open', label: 'Open' },
  { value: 'waiting_on_client', label: 'Waiting on client' },
  { value: 'resolved', label: 'Resolved' },
  { value: 'closed', label: 'Closed' },
];

export function supportCategoryLabel(v: string | null | undefined): string {
  return SUPPORT_CATEGORIES.find((c) => c.value === v)?.label ?? 'Request';
}

export function supportPriorityLabel(v: string | null | undefined): string {
  return SUPPORT_PRIORITIES.find((c) => c.value === v)?.label ?? 'Normal';
}

/** The workspace sees "Awaiting your reply" where support sees "Waiting on client". */
export function supportStatusLabel(v: string | null | undefined, forClient = false): string {
  if (forClient && v === 'waiting_on_client') return 'Awaiting your reply';
  if (forClient && v === 'open') return 'With support';
  return SUPPORT_STATUSES.find((c) => c.value === v)?.label ?? 'Open';
}

export function supportStatusBadge(v: string | null | undefined): string {
  switch (v) {
    case 'open':
      return 'nd-badge-blue';
    case 'waiting_on_client':
      return 'nd-badge-amber';
    case 'resolved':
      return 'nd-badge-green';
    default:
      return 'nd-badge-gray';
  }
}

export function supportPriorityBadge(v: string | null | undefined): string {
  switch (v) {
    case 'urgent':
      return 'nd-badge-red';
    case 'high':
      return 'nd-badge-amber';
    default:
      return 'nd-badge-gray';
  }
}
