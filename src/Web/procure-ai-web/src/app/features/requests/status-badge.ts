import { Component, computed, input } from '@angular/core';
import { RequestStatus } from '../../api/requests/models/request-status';

const STYLES: Record<RequestStatus, string> = {
  Draft: 'bg-slate-100 text-slate-700 ring-slate-300',
  Submitted: 'bg-sky-50 text-sky-800 ring-sky-300',
  Approved: 'bg-emerald-50 text-emerald-800 ring-emerald-300',
  Ordered: 'bg-indigo-50 text-indigo-800 ring-indigo-300',
  Completed: 'bg-teal-50 text-teal-800 ring-teal-300',
  Rejected: 'bg-rose-50 text-rose-800 ring-rose-300',
  Cancelling: 'bg-amber-50 text-amber-800 ring-amber-300',
  Cancelled: 'bg-zinc-100 text-zinc-600 ring-zinc-300',
};
const FALLBACK = 'bg-slate-100 text-slate-700 ring-slate-300';

/** The status of a request as a small pill. Always one line; the text carries the meaning, colour only helps. */
@Component({
  selector: 'app-status-badge',
  template: `<span [class]="classes()">{{ status() }}</span>`,
})
export class StatusBadge {
  readonly status = input.required<RequestStatus>();
  protected readonly classes = computed(
    () => `inline-flex items-center whitespace-nowrap rounded-full px-2.5 py-0.5 text-xs font-semibold ring-1 ring-inset ${STYLES[this.status()] ?? FALLBACK}`,
  );
}
