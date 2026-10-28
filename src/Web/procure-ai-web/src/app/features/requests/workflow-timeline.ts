import { Component, input } from '@angular/core';
import { ApprovalChainDto } from '../../api/approvals/models/approval-chain-dto';
import { ApprovalStepDto } from '../../api/approvals/models/approval-step-dto';
import { BusinessEventDto } from '../../api/audit/models/business-event-dto';
import { InvoiceDto } from '../../api/invoicing/models/invoice-dto';
import { PurchaseOrderDto } from '../../api/purchasing/models/purchase-order-dto';
import { RequestDto } from '../../api/requests/models/request-dto';

/**
 * done = happened, active = happening now, warn = needs attention (moved up, retrying, mismatch), failed = ended badly,
 * pending = still to come (or never needed), covered = an approval step folded into another one.
 */
export type StepState = 'done' | 'active' | 'warn' | 'failed' | 'pending' | 'covered';

export interface TimelineStep {
  key: string;
  state: StepState;
  title: string;
  detail: string | null;
  /** ISO time of the step, when there is one. */
  at: string | null;
}

export interface TimelineInput {
  request: RequestDto;
  /** The approval chain; null while the request has none (a draft). */
  chain: ApprovalChainDto | null;
  /** The purchase order; null when there is none yet or the caller may not read orders. */
  order: PurchaseOrderDto | null;
  invoices: InvoiceDto[];
  history: BusinessEventDto[];
  canSeeOrder: boolean;
  canSeeInvoices: boolean;
}

const LEVEL_RANK: Record<string, number> = { Manager: 0, DepartmentHead: 1, Finance: 2 };

export function levelLabel(level: string): string {
  return level === 'DepartmentHead' ? 'Department head' : level;
}

/** Wall-clock time in the user's timezone, e.g. `28 Oct, 09:40`. */
export function formatWhen(iso: string): string {
  return new Intl.DateTimeFormat('en-ZA', { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit', hour12: false }).format(new Date(iso));
}

/** The workflow of one request, from what the services answered. Pure: the same input always gives the same steps. */
export function buildTimeline(input: TimelineInput): TimelineStep[] {
  const { request, chain, order, invoices, canSeeOrder, canSeeInvoices } = input;
  const events = [...input.history].sort((a, b) => a.occurredAt.localeCompare(b.occurredAt));
  const last = (type: string) => events.filter((e) => e.type === type).at(-1);
  const status = request.status;
  const terminal = status === 'Rejected' || status === 'Cancelled';
  const out: TimelineStep[] = [];
  const add = (key: string, state: StepState, title: string, detail: string | null = null, at: string | null = null) => out.push({ key, state, title, detail, at });

  // 1. Submitted
  const submittedEvent = last('RequestSubmitted');
  if (request.submittedAt || submittedEvent) {
    const who = submittedEvent?.actorName;
    add('submitted', 'done', 'Submitted', who ? `Submitted by ${who}` : null, request.submittedAt ?? submittedEvent!.occurredAt);
  } else {
    add('submitted', 'pending', 'Submitted', 'Not submitted yet');
  }
  const submitted = status !== 'Draft';

  // 2. Budget
  const reserved = last('BudgetReserved');
  const refused = last('BudgetReservationFailed');
  const hasChain = (chain?.steps.length ?? 0) > 0;
  if (refused && (!reserved || reserved.occurredAt < refused.occurredAt)) {
    add('budget', 'failed', 'Budget refused', refused.summary, refused.occurredAt);
  } else if (reserved) {
    add('budget', 'done', 'Budget reserved', reserved.summary, reserved.occurredAt);
  } else if (!submitted) {
    add('budget', 'pending', 'Budget reserved');
  } else if (!terminal && !hasChain && status === 'Submitted') {
    add('budget', 'active', 'Reserving budget');
  } else if (!terminal && hasChain) {
    add('budget', 'done', 'Budget reserved');
  }

  // 3. Approval steps
  if (hasChain) {
    approvalSteps(chain!, status, events, add);
  } else if (!terminal && !refused) {
    const passed = status === 'Approved' || status === 'Ordered' || status === 'Completed' || status === 'Cancelling';
    add('approval', passed ? 'done' : 'pending', 'Approval');
  }

  // 4. Everything after approval, until the request ended
  if (!terminal && !refused) {
    orderSteps(request, order, canSeeOrder, add);
    invoiceSteps(request, invoices, canSeeInvoices, add);
  }

  // 5. How it ended
  const released = last('BudgetReleased');
  if (terminal && released) {
    add('released', 'done', 'Budget released', released.summary, released.occurredAt);
  }
  if (status === 'Rejected') {
    add('rejected', 'failed', 'Rejected', request.rejectionReason, request.rejectedAt);
  } else if (status === 'Cancelled') {
    add('cancelled', 'failed', 'Cancelled', request.cancellationReason, request.cancelledAt);
  } else if (status === 'Cancelling') {
    add('cancelling', 'active', 'Cancelling', 'Waiting for Purchasing to stop the order');
  } else if (status === 'Completed') {
    add('completed', 'done', 'Completed', request.spentInclVatDisplay ? `${request.spentInclVatDisplay} spent` : null, last('RequestCompleted')?.occurredAt ?? null);
  }
  if (request.cancellationRefusedReason && !terminal) {
    add('refused', 'warn', 'Cancellation refused', request.cancellationRefusedReason);
  }
  return out;
}

type Add = (key: string, state: StepState, title: string, detail?: string | null, at?: string | null) => void;

function approvalSteps(chain: ApprovalChainDto, status: string, events: BusinessEventDto[], add: Add): void {
  // Same step number: the step that was moved up comes first, then the one that took over.
  const steps = [...chain.steps].sort((a, b) => a.stepNo - b.stepNo || Number(b.status === 'Escalated') - Number(a.status === 'Escalated'));
  // The chain is over for a cancelled or rejected request: its cancelled steps were never "merged".
  const ended = ['Cancelled', 'Rejected', 'Cancelling'].includes(chain.state) || status === 'Cancelled' || status === 'Rejected';
  const open = steps.find((s) => s.status === 'Pending');
  // Audit says which step an approval event is about (`stepNo`) and who acted (`actorName`); no sentence is read here.
  const forStep = (type: string, step: ApprovalStepDto) => events.filter((e) => e.type === type && e.stepNo === step.stepNo).at(-1);

  steps.forEach((step, index) => {
    const title = `${levelLabel(step.level)} approval`;
    const key = `step-${step.id}`;
    switch (step.status) {
      case 'Approved': {
        const approved = forStep('ApprovalStepApproved', step);
        add(key, 'done', title, approved ? (approved.actorName ? `Approved by ${approved.actorName}` : 'Approved') : null, approved?.occurredAt ?? null);
        break;
      }
      case 'Pending': {
        if (step !== open) {
          add(key, 'pending', title);
          break;
        }
        const parts = ['Waiting for approval'];
        if (forStep('ApprovalReminderDue', step)) {
          parts.push('reminder sent');
        }
        if (step.dueAt) {
          parts.push(`escalates ${formatWhen(step.dueAt)}`);
        }
        add(key, 'active', title, parts.join(' · '));
        break;
      }
      case 'Escalated': {
        const next = steps.find((s) => s.stepNo === step.stepNo && s.status !== 'Escalated');
        add(key, 'warn', title, `No answer in time, moved up${next ? ` to ${levelLabel(next.level)}` : ''}`);
        break;
      }
      case 'Rejected':
        add(key, 'failed', title, 'Rejected');
        break;
      default: {
        // Cancelled. The chain DTO has no "merged" flag, so a Cancelled step in a chain that is still running is a step an
        // escalated step took over (plan 02 ruling 26: a merged step is stored as Cancelled); the step that covers it is derived below.
        if (ended) {
          add(key, 'pending', title, 'Did not take place');
          break;
        }
        const rank = LEVEL_RANK[step.level] ?? 0;
        const cover = steps.slice(0, index).filter((s) => s.stepNo < step.stepNo && (s.status === 'Pending' || s.status === 'Approved') && (LEVEL_RANK[s.level] ?? 0) >= rank).at(-1);
        add(key, 'covered', title, cover ? `Covered by the ${levelLabel(cover.level)} step above` : 'Covered by an earlier step');
      }
    }
  });
}

function orderSteps(request: RequestDto, order: PurchaseOrderDto | null, canSeeOrder: boolean, add: Add): void {
  const status = request.status;
  if (canSeeOrder && order) {
    const retrying = order.status === 'PendingSupplier' && (order.supplierAttempts > 0 || !!order.supplierLastError);
    const state: StepState = retrying
      ? 'warn'
      : order.status === 'PendingSupplier' || order.status === 'SentToSupplier'
        ? 'active'
        : order.status === 'Rejected' || order.status === 'Cancelled'
          ? 'failed'
          : 'done';
    const detail = retrying
      ? [order.supplierStatus, `attempt ${order.supplierAttempts}`, order.supplierLastError].filter(Boolean).join(' · ')
      : order.supplierStatus;
    add('order', state, `Purchase order ${order.number}`, detail, order.createdAt);

    const ordered = order.lines.reduce((sum, l) => sum + l.ordered, 0);
    const received = order.lines.reduce((sum, l) => sum + l.received, 0);
    const receivedAt = order.receipts.at(-1)?.receivedAt ?? null;
    if (order.status === 'Received') {
      add('delivery', 'done', 'Delivery', `${received} of ${ordered} received`, receivedAt);
    } else if (order.status === 'ClosedShort') {
      add('delivery', 'done', 'Delivery', `Closed short: ${received} of ${ordered} received`, receivedAt);
    } else if (order.status === 'PartiallyReceived') {
      add('delivery', 'active', 'Delivery', `${received} of ${ordered} received`, receivedAt);
    } else if (order.status === 'Confirmed') {
      add('delivery', 'active', 'Delivery', 'Waiting for delivery');
    } else {
      add('delivery', 'pending', 'Delivery');
    }
    return;
  }
  if (request.orderNumber) {
    add('order', 'done', `Purchase order ${request.orderNumber}`, 'Placed with the supplier');
  } else if (status === 'Approved') {
    add('order', 'active', 'Purchase order', 'Being created');
  } else {
    add('order', 'pending', 'Purchase order');
  }
  add('delivery', status === 'Completed' ? 'done' : 'pending', 'Delivery');
}

function invoiceSteps(request: RequestDto, invoices: InvoiceDto[], canSee: boolean, add: Add): void {
  if (!canSee || invoices.length === 0) {
    add('invoices', request.status === 'Completed' ? 'done' : 'pending', 'Invoice, match and payment');
    return;
  }
  for (const invoice of invoices) {
    const parts: string[] = [];
    let state: StepState;
    switch (invoice.status) {
      case 'Paid':
        state = 'done';
        parts.push('Matched and paid');
        break;
      case 'Matched':
      case 'PartiallyPaid':
        state = 'active';
        parts.push(invoice.status === 'Matched' ? 'Matched, waiting for payment' : 'Partly paid');
        break;
      case 'Mismatched':
        state = 'warn';
        parts.push(['Does not match', ...invoice.mismatchReasons].join(': '));
        break;
      default:
        state = 'warn';
        parts.push('Waiting for its order');
    }
    if (invoice.onHold) {
      state = 'warn';
      parts.push(invoice.holdReason ? `on hold: ${invoice.holdReason}` : 'on hold');
    }
    for (const payment of invoice.payments) {
      parts.push(`payment ${payment.number} ${payment.exportStatus === 'Exported' ? `exported${payment.erpReference ? ` (${payment.erpReference})` : ''}` : 'waiting for export'}`);
    }
    add(`invoice-${invoice.id}`, state, `Invoice ${invoice.supplierInvoiceNumber}`, parts.join(' · '), invoice.receivedAt);
  }
}

const MARKER: Record<StepState, string> = {
  done: 'bg-emerald-600 text-white',
  active: 'bg-indigo-700 text-white ring-4 ring-indigo-100',
  warn: 'bg-amber-500 text-white',
  failed: 'bg-rose-600 text-white',
  pending: 'border-2 border-slate-300 bg-white text-slate-400',
  covered: 'border-2 border-dashed border-slate-300 bg-slate-50 text-slate-400',
};
const STATE_WORD: Record<StepState, string> = { done: 'Done', active: 'In progress', warn: 'Needs attention', failed: 'Failed', pending: 'Not yet', covered: 'Covered' };

/** The workflow as a vertical list of steps. The state is also written out (screen readers, colour-blind users). */
@Component({
  selector: 'app-workflow-timeline',
  template: `
    <ol class="m-0 list-none p-0" aria-label="Workflow">
      @for (step of steps(); track step.key) {
        <li class="group relative pb-5 pl-10 last:pb-0" [attr.data-state]="step.state">
          <span class="absolute top-7 bottom-0 left-3.5 w-px bg-slate-200 group-last:hidden" aria-hidden="true"></span>
          <span class="absolute top-0 left-0 inline-flex size-7 items-center justify-center rounded-full text-xs font-bold" [class]="marker[step.state]" aria-hidden="true">
            @switch (step.state) {
              @case ('done') {
                <svg viewBox="0 0 20 20" class="size-4" fill="none" stroke="currentColor" stroke-width="2.5"><path d="m4 10.5 4 4 8-9" /></svg>
              }
              @case ('failed') {
                <svg viewBox="0 0 20 20" class="size-4" fill="none" stroke="currentColor" stroke-width="2.5"><path d="m5 5 10 10M15 5 5 15" /></svg>
              }
              @case ('warn') {
                <svg viewBox="0 0 20 20" class="size-4" fill="none" stroke="currentColor" stroke-width="2.5"><path d="M10 5v6m0 3.5v.01" /></svg>
              }
              @case ('active') {
                <span class="size-2 rounded-full bg-white"></span>
              }
              @case ('covered') {
                <svg viewBox="0 0 20 20" class="size-4" fill="none" stroke="currentColor" stroke-width="2.5"><path d="M5 10h10" /></svg>
              }
            }
          </span>
          <div class="flex flex-wrap items-baseline justify-between gap-x-3">
            <p class="font-medium" [class]="step.state === 'pending' || step.state === 'covered' ? 'text-slate-500' : 'text-slate-900'">
              {{ step.title }} <span class="sr-only">({{ stateWord[step.state] }})</span>
            </p>
            @if (step.at) {
              <time class="text-xs whitespace-nowrap text-slate-500" [attr.datetime]="step.at">{{ when(step.at) }}</time>
            }
          </div>
          @if (step.detail) {
            <p class="mt-0.5 text-sm break-words" [class]="step.state === 'warn' ? 'text-amber-800' : step.state === 'failed' ? 'text-rose-800' : 'text-slate-600'">{{ step.detail }}</p>
          }
        </li>
      }
    </ol>
  `,
})
export class WorkflowTimeline {
  readonly steps = input.required<TimelineStep[]>();
  protected readonly marker = MARKER;
  protected readonly stateWord = STATE_WORD;
  protected readonly when = formatWhen;
}
