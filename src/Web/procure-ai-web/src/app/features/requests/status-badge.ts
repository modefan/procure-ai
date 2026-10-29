import { Component, computed, input } from '@angular/core';

/** The five looks of a status pill. The text always carries the meaning; colour only helps. */
export type StatusGroup = 'neutral' | 'info' | 'warn' | 'ok' | 'bad';

const GROUP_CLASSES: Record<StatusGroup, string> = {
  neutral: 'bg-slate-100 text-slate-700 ring-slate-300',
  info: 'bg-sky-50 text-sky-800 ring-sky-300',
  warn: 'bg-amber-50 text-amber-800 ring-amber-300',
  ok: 'bg-emerald-50 text-emerald-800 ring-emerald-300',
  bad: 'bg-rose-50 text-rose-800 ring-rose-300',
};

/**
 * Every status the APIs return, by the colour group it belongs to:
 * neutral = draft / pending, info = in progress, warn = waiting / held / mismatch / retrying,
 * ok = done / matched / paid, bad = rejected / cancelled / failed.
 */
const STATUS_GROUPS: Record<string, StatusGroup> = {
  // Purchase request
  Draft: 'neutral',
  Submitted: 'info',
  Approved: 'ok',
  Ordered: 'info',
  Completed: 'ok',
  Rejected: 'bad',
  Cancelling: 'warn',
  Cancelled: 'bad',
  // Approval step (Pending, Approved, Rejected, Cancelled above; Merged is a step folded into another one)
  Pending: 'neutral',
  Escalated: 'warn',
  Merged: 'neutral',
  // Purchase order
  PendingSupplier: 'warn',
  SentToSupplier: 'info',
  Confirmed: 'info',
  PartiallyReceived: 'info',
  Received: 'ok',
  ClosedShort: 'warn',
  // Invoice and match
  AwaitingOrder: 'warn',
  Matched: 'ok',
  Mismatched: 'warn',
  PartiallyPaid: 'info',
  Paid: 'ok',
  // Payment export
  Exported: 'ok',
  'Export pending': 'warn',
  // Generic words other screens use
  InProgress: 'info',
  Waiting: 'warn',
  Held: 'warn',
  Retrying: 'warn',
  Failed: 'bad',
};

/**
 * The supplier side of an order is one line of text from Purchasing (not an enum), so it is grouped by how it starts.
 * Order matters: the first match wins.
 */
const SUPPLIER_TEXT: readonly (readonly [prefix: string, group: StatusGroup])[] = [
  ['Supplier unavailable', 'warn'],
  ['Waiting for supplier', 'warn'],
  ['Sent to supplier', 'info'],
  ['Rejected by supplier', 'bad'],
  ['Cancelled, telling the supplier', 'warn'],
  ['Confirmed,', 'ok'],
];

/** The colour group of a status; anything not known is neutral. */
export function statusGroup(status: string): StatusGroup {
  const exact = STATUS_GROUPS[status];
  if (exact) {
    return exact;
  }
  return SUPPLIER_TEXT.find(([prefix]) => status.startsWith(prefix))?.[1] ?? 'neutral';
}

/** `PartiallyReceived` becomes `Partially received`; words and sentences stay as they are. */
function words(status: string): string {
  return /^[A-Z][A-Za-z]*$/.test(status) ? status.replace(/([a-z])([A-Z])/g, (_, a: string, b: string) => `${a} ${b.toLowerCase()}`) : status;
}

/**
 * A status as a small pill: requests, approval steps, orders (and their supplier line), invoices, matches and
 * payments. Pass the API's status value as it is. Always one line.
 */
@Component({
  selector: 'app-status-badge',
  template: `<span [class]="classes()">{{ label() }}</span>`,
})
export class StatusBadge {
  /** The status exactly as the API sent it. */
  readonly status = input.required<string>();
  protected readonly label = computed(() => words(this.status()));
  protected readonly classes = computed(
    () => `inline-flex items-center whitespace-nowrap rounded-full px-2.5 py-0.5 text-xs font-semibold ring-1 ring-inset ${GROUP_CLASSES[statusGroup(this.status())]}`,
  );
}
