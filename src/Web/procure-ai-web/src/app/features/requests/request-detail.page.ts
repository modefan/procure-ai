import { Tab, TabContent, TabList, TabPanel, Tabs } from '@angular/aria/tabs';
import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { Title } from '@angular/platform-browser';
import { RouterLink } from '@angular/router';
import { RequestDto } from '../../api/requests/models/request-dto';
import { PresenceStore } from '../../core/live/presence.store';
import { BusyButton } from '../../core/ui/busy-button';
import { ConfirmDialog } from '../../core/ui/confirm-dialog';
import { KeepRandPipe } from '../../core/ui/money.pipe';
import { AuditTrail } from './audit-trail';
import { DataChangesTab } from './data-changes-tab';
import { DataChangesTabStore } from './data-changes-tab.store';
import { RequestDetailStore } from './request-detail.store';
import { StatusBadge } from './status-badge';
import { WorkflowTimeline } from './workflow-timeline';

/** The longest cancellation reason the Requests API accepts (the OpenAPI document does not state it). */
const CANCEL_REASON_MAX = 500;

/**
 * Screen 7: one request with its workflow, items, order, a History / Data changes tab and who else is looking at it (needs `requests.read`).
 * The route resolver passes the request it already loaded (`request`); everything else loads in parallel here and
 * refreshes by itself when the server says something about this request changed.
 */
@Component({
  selector: 'app-request-detail-page',
  imports: [StatusBadge, WorkflowTimeline, AuditTrail, BusyButton, RouterLink, KeepRandPipe, Tabs, TabList, Tab, TabPanel, TabContent, DataChangesTab],
  providers: [RequestDetailStore, DataChangesTabStore],
  template: `
    <section class="mx-auto max-w-6xl px-4 py-8">
      <a routerLink="/app/requests" class="text-sm font-medium text-indigo-700 hover:underline">&larr; All requests</a>

      @if (store.request(); as request) {
        <header class="mt-3 flex flex-wrap items-start justify-between gap-x-6 gap-y-4">
          <div class="min-w-0">
            <p class="text-sm font-semibold text-slate-600">
              {{ request.number }}
              @if (store.requestedBy(); as by) {
                <span class="font-normal"> · Requested by {{ by }}</span>
              }
            </p>
            <h1 class="text-2xl font-bold tracking-tight break-words text-slate-900">{{ request.title }}</h1>
            <div class="mt-3 flex flex-wrap items-center gap-x-4 gap-y-2">
              <span class="text-xl font-semibold whitespace-nowrap tabular-nums text-slate-900">{{ request.totalInclVatDisplay }}</span>
              <app-status-badge [status]="request.status" />
              @if (activeStep(); as step) {
                <span class="text-sm text-slate-600">Now: {{ step.title }}</span>
              }
              @if (viewersText(); as viewing) {
                <span class="inline-flex items-center gap-1.5 text-sm text-slate-700" role="status">
                  <svg viewBox="0 0 24 24" class="size-4 text-indigo-700" fill="none" stroke="currentColor" stroke-width="2" aria-hidden="true">
                    <path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7S2 12 2 12Z" /><circle cx="12" cy="12" r="3" />
                  </svg>
                  {{ viewing }}
                </span>
              }
            </div>
          </div>
          <div class="flex flex-wrap items-center gap-3">
            @if (store.canEdit()) {
              <a [routerLink]="['/app/requests', request.id, 'edit']" class="btn-secondary">Edit draft</a>
            }
            @if (store.canCancel()) {
              <app-busy-button variant="secondary" [busy]="store.busy()" busyLabel="Cancelling…" (pressed)="cancel()">Cancel request</app-busy-button>
            }
          </div>
        </header>

        @if (store.generalError(); as message) {
          <div role="alert" class="mt-6 rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-900">{{ message | keepRand }}</div>
        }
        @if (store.partialFailures().length > 0) {
          <div role="status" class="mt-6 flex flex-wrap items-center justify-between gap-3 rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-900">
            <span>Some details could not be loaded: {{ store.partialFailures().join(', ') }}.</span>
            <button type="button" class="btn-secondary" (click)="store.reload()">Try again</button>
          </div>
        }

        <section class="card mt-6" aria-labelledby="workflow-heading">
          <h2 id="workflow-heading" class="mb-4 text-base font-semibold text-slate-900">Workflow</h2>
          <app-workflow-timeline [steps]="store.timeline()" />
        </section>

        <section class="card mt-6" aria-labelledby="items-heading">
          <h2 id="items-heading" class="mb-3 text-base font-semibold text-slate-900">Items</h2>
          <table class="w-full table-fixed text-left text-sm">
            <caption class="sr-only">Items on {{ request.number }}</caption>
            <thead class="border-b border-slate-200 text-xs font-semibold tracking-wide text-slate-600 uppercase">
              <tr>
                <th scope="col" class="py-2 pr-3">Item</th>
                <th scope="col" class="w-16 py-2 pr-3 text-right whitespace-nowrap">Qty</th>
                <th scope="col" class="hidden w-32 py-2 pr-3 text-right whitespace-nowrap sm:table-cell">Unit price</th>
                <th scope="col" class="w-28 py-2 text-right whitespace-nowrap sm:w-32">Total</th>
              </tr>
            </thead>
            <tbody class="divide-y divide-slate-100">
              @for (line of request.lines; track line.lineNo) {
                <tr>
                  <td class="truncate py-2 pr-3 text-slate-800" [title]="line.description">{{ line.description }}</td>
                  <td class="py-2 pr-3 text-right tabular-nums">{{ line.quantity }}</td>
                  <td class="hidden py-2 pr-3 text-right whitespace-nowrap tabular-nums sm:table-cell">{{ line.unitPriceExclVatDisplay }}</td>
                  <td class="py-2 text-right whitespace-nowrap tabular-nums">{{ line.totalExclVatDisplay }}</td>
                </tr>
              }
            </tbody>
            <tfoot class="text-sm">
              <tr class="border-t border-slate-200">
                <td class="pt-3 text-slate-600" colspan="2">Total excl. VAT</td>
                <td class="hidden sm:table-cell"></td>
                <td class="pt-3 text-right whitespace-nowrap tabular-nums">{{ request.totalExclVatDisplay }}</td>
              </tr>
              <tr>
                <td class="text-slate-600" colspan="2">VAT</td>
                <td class="hidden sm:table-cell"></td>
                <td class="text-right whitespace-nowrap tabular-nums">{{ request.vatDisplay }}</td>
              </tr>
              <tr class="font-semibold">
                <td colspan="2">Total incl. VAT</td>
                <td class="hidden sm:table-cell"></td>
                <td class="text-right whitespace-nowrap tabular-nums">{{ request.totalInclVatDisplay }}</td>
              </tr>
            </tfoot>
          </table>
        </section>

        @if (store.canSeeOrder() && store.order(); as order) {
          <section class="card mt-6" aria-labelledby="order-heading">
            <div class="mb-3 flex flex-wrap items-center justify-between gap-2">
              <h2 id="order-heading" class="text-base font-semibold text-slate-900">Order and delivery</h2>
              <app-status-badge [status]="order.status" />
            </div>
            <p class="text-sm text-slate-700">
              <span class="font-medium">{{ order.number }}</span> with {{ order.supplierId }} · {{ order.supplierStatus }}
              @if (order.supplierReference) {
                · reference {{ order.supplierReference }}
              }
            </p>
            <table class="mt-3 w-full table-fixed text-left text-sm">
              <caption class="sr-only">Order lines of {{ order.number }}</caption>
              <thead class="border-b border-slate-200 text-xs font-semibold tracking-wide text-slate-600 uppercase">
                <tr>
                  <th scope="col" class="py-2 pr-3">Item</th>
                  <th scope="col" class="w-20 py-2 pr-3 text-right whitespace-nowrap">Ordered</th>
                  <th scope="col" class="w-20 py-2 pr-3 text-right whitespace-nowrap">Received</th>
                  <th scope="col" class="hidden w-24 py-2 text-right whitespace-nowrap sm:table-cell">Outstanding</th>
                </tr>
              </thead>
              <tbody class="divide-y divide-slate-100">
                @for (line of order.lines; track line.lineNo) {
                  <tr>
                    <td class="truncate py-2 pr-3 text-slate-800" [title]="line.description">{{ line.description }}</td>
                    <td class="py-2 pr-3 text-right tabular-nums">{{ line.ordered }}</td>
                    <td class="py-2 pr-3 text-right tabular-nums">{{ line.received }}</td>
                    <td class="hidden py-2 text-right tabular-nums sm:table-cell">{{ line.outstanding }}</td>
                  </tr>
                }
              </tbody>
            </table>
          </section>
        }

        <section class="card mt-6" aria-label="History and data changes">
          <div ngTabs>
            <div ngTabList selectionMode="follow" [(selectedTab)]="tab" class="mb-4 flex gap-1 overflow-x-auto border-b border-slate-200" aria-label="History and data changes">
              <button ngTab value="history" type="button" class="tab-trigger">History</button>
              <button ngTab value="data-changes" type="button" class="tab-trigger">Data changes</button>
            </div>
            <div ngTabPanel value="history" [preserveContent]="true">
              <ng-template ngTabContent>
                <h2 id="audit-heading" class="sr-only">Audit trail</h2>
                <app-audit-trail [events]="store.history()" />
              </ng-template>
            </div>
            <div ngTabPanel value="data-changes" [preserveContent]="true">
              <ng-template ngTabContent>
                <h2 class="sr-only">Data changes</h2>
                <app-data-changes-tab [requestId]="id()" />
              </ng-template>
            </div>
          </div>
        </section>
      } @else if (store.error(); as message) {
        <div role="alert" class="mt-6 flex flex-wrap items-center justify-between gap-3 rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-900">
          <span>{{ message }}</span>
          <button type="button" class="btn-secondary" (click)="retry()">Try again</button>
        </div>
      } @else {
        <div class="card mt-6 space-y-4" role="status" aria-live="polite">
          <span class="sr-only">Loading the request</span>
          @for (row of skeletonRows; track row) {
            <div class="h-6 animate-pulse rounded bg-slate-200" aria-hidden="true"></div>
          }
        </div>
      }
    </section>
  `,
})
export class RequestDetailPage {
  /** The route's `:id`. */
  readonly id = input.required<string>();
  /** What the route resolver loaded (null when it could not). */
  readonly request = input<RequestDto | null>(null);

  protected readonly store = inject(RequestDetailStore);
  private readonly presence = inject(PresenceStore);
  private readonly confirm = inject(ConfirmDialog);
  private readonly title = inject(Title);
  protected readonly skeletonRows = [1, 2, 3, 4, 5];
  /** The open tab below the details: the business history, or the field-level data changes (loaded when first opened). */
  protected readonly tab = signal<string | undefined>('history');

  protected readonly activeStep = computed(() => this.store.timeline().find((s) => s.state === 'active' && s.key !== 'cancelling') ?? null);
  private readonly viewers = computed(() => this.presence.viewersOf('request', this.id())());
  protected readonly viewersText = computed(() => {
    const names = this.viewers().map((v) => v.name);
    switch (names.length) {
      case 0:
        return null;
      case 1:
        return `${names[0]} is also viewing`;
      case 2:
        return `${names[0]} and ${names[1]} are also viewing`;
      default:
        return `${names[0]}, ${names[1]} and ${names.length - 2} ${names.length === 3 ? 'other' : 'others'} are also viewing`;
    }
  });

  constructor() {
    effect(() => {
      const id = this.id();
      untracked(() => void this.store.init(id, this.request()?.id === id ? this.request() : null));
    });
    // The tab says which request this is (the route title is only "Request").
    effect(() => {
      const number = this.store.request()?.number;
      if (number) {
        this.title.setTitle(`${number} - Request`);
      }
    });
    // Tell the hub which request is open (so others see "is also viewing"); stop when the id changes or the page goes.
    effect((onCleanup) => {
      onCleanup(this.presence.watch('request', this.id()));
    });
  }

  protected retry(): void {
    void this.store.init(this.id(), null);
  }

  protected async cancel(): Promise<void> {
    const request = this.store.request();
    if (!request) {
      return;
    }
    const answer = await this.confirm.askWithInput({
      title: `Cancel ${request.number}?`,
      body: 'The request stops here and any budget reserved for it is released. This cannot be undone.',
      confirm: 'Cancel request',
      cancel: 'Keep request',
      input: { label: 'Reason (optional)', minLength: 0, maxLength: CANCEL_REASON_MAX, required: false },
    });
    if (answer.confirmed) {
      await this.store.cancel(answer.value);
    }
  }
}
