import { NgTemplateOutlet } from '@angular/common';
import { Component, DestroyRef, inject, OnInit, signal } from '@angular/core';
import { form, FormField, maxLength } from '@angular/forms/signals';
import { RouterLink } from '@angular/router';
import { ApprovalStepDto } from '../../api/approvals/models/approval-step-dto';
import { BusyButton } from '../../core/ui/busy-button';
import { Pager } from '../../core/ui/pager';
import { ApprovalsStore } from './approvals.store';
import { escalatesIn, waitedFor } from './inbox-time';

const MAX_REASON = 500;

/**
 * Screen 8: the approvals inbox (needs `requests.approve`). The open steps I may act on, most urgent first. Approve
 * sends the step's version as `If-Match`; Reject opens a reason box first. The row is busy while the server works
 * and leaves the list when it agrees; a 412 opens the conflict dialog (from the store) and the list reloads.
 * A table from the lg breakpoint (the columns need it), cards below.
 */
@Component({
  selector: 'app-approvals-page',
  imports: [RouterLink, FormField, BusyButton, Pager, NgTemplateOutlet],
  template: `
    <section class="mx-auto max-w-6xl px-4 py-8">
      <h1 class="text-2xl font-bold tracking-tight text-slate-900">Approvals</h1>
      <p class="mt-1 text-slate-600">Requests waiting for you. Amounts include VAT.</p>

      @if (store.error(); as message) {
        <div role="alert" class="mt-6 flex flex-wrap items-center justify-between gap-3 rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-900">
          <span>{{ message }}</span>
          <button type="button" class="btn-secondary" (click)="store.load(store.page())">Try again</button>
        </div>
      }

      @if (store.isLoading() && store.entities().length === 0) {
        <div class="card mt-6 space-y-4" role="status" aria-live="polite">
          <span class="sr-only">Loading approvals</span>
          @for (row of skeletonRows; track row) {
            <div class="h-6 animate-pulse rounded bg-slate-200" aria-hidden="true"></div>
          }
        </div>
      } @else if (store.entities().length === 0 && !store.error()) {
        <div class="card mt-6 py-12 text-center">
          <p class="text-lg font-semibold text-slate-900">Nothing is waiting for your approval</p>
          <p class="mt-1 text-slate-600">New requests for you appear here by themselves.</p>
        </div>
      } @else if (store.entities().length > 0) {
        <div class="mt-6" [attr.aria-busy]="store.isLoading()" [class.opacity-60]="store.isLoading()">
          <div class="hidden overflow-hidden rounded-xl border border-slate-200 bg-white shadow-sm lg:block">
            <table class="w-full table-fixed text-left text-sm">
              <caption class="sr-only">Requests waiting for your approval, page {{ store.page() }} of {{ store.pageCount() }}</caption>
              <colgroup>
                <col class="w-24" />
                <col />
                <col class="w-36 lg:w-40" />
                <col class="w-28 lg:w-32" />
                <col class="w-20" />
                <col class="w-24 lg:w-28" />
                <col class="w-52" />
              </colgroup>
              <thead class="border-b border-slate-200 bg-slate-50 text-xs font-semibold uppercase tracking-wide text-slate-600">
                <tr>
                  <th scope="col" class="px-4 py-3 whitespace-nowrap">Number</th>
                  <th scope="col" class="px-4 py-3 whitespace-nowrap">Title</th>
                  <th scope="col" class="px-4 py-3 whitespace-nowrap">Requester</th>
                  <th scope="col" class="px-4 py-3 text-right whitespace-nowrap">Amount</th>
                  <th scope="col" class="px-4 py-3 whitespace-nowrap">Waiting</th>
                  <th scope="col" class="px-4 py-3 whitespace-nowrap">Escalates</th>
                  <th scope="col" class="px-4 py-3"><span class="sr-only">Actions</span></th>
                </tr>
              </thead>
              <tbody class="divide-y divide-slate-100">
                @for (step of store.entities(); track step.id) {
                  <tr [attr.data-step]="step.id" class="align-top hover:bg-slate-50">
                    <td class="px-4 py-3 font-medium whitespace-nowrap"><a [routerLink]="['/app/requests', step.requestId]" class="text-indigo-800 hover:underline">{{ step.requestNumber }}</a></td>
                    <td class="truncate px-4 py-3 text-slate-800" [title]="step.requestTitle">{{ step.requestTitle }}</td>
                    <td class="truncate px-4 py-3 text-slate-700" [title]="requesterOf(step)">{{ requesterOf(step) }}</td>
                    <td class="px-4 py-3 text-right whitespace-nowrap tabular-nums text-slate-900">{{ step.totalInclVatDisplay }}</td>
                    <td class="px-4 py-3 whitespace-nowrap text-slate-700">{{ waitingOf(step) }}</td>
                    <td class="px-4 py-3 whitespace-nowrap text-slate-700">{{ escalationOf(step) }}</td>
                    <td class="px-4 py-3"><ng-container [ngTemplateOutlet]="actions" [ngTemplateOutletContext]="{ $implicit: step }" /></td>
                  </tr>
                  @if (store.errorOf(step.id) || rejecting() === step.id) {
                    <tr [attr.data-extra]="step.id" class="bg-slate-50">
                      <td colspan="7" class="px-4 pb-4 pt-0"><ng-container [ngTemplateOutlet]="extra" [ngTemplateOutletContext]="{ $implicit: step, area: 'table' }" /></td>
                    </tr>
                  }
                }
              </tbody>
            </table>
          </div>

          <ul class="space-y-3 lg:hidden" aria-label="Requests waiting for your approval">
            @for (step of store.entities(); track step.id) {
              <li class="card p-4">
                <div class="flex items-center justify-between gap-3">
                  <a [routerLink]="['/app/requests', step.requestId]" class="font-semibold whitespace-nowrap text-indigo-800 hover:underline">{{ step.requestNumber }}</a>
                  <span class="font-semibold whitespace-nowrap tabular-nums text-slate-900">{{ step.totalInclVatDisplay }}</span>
                </div>
                <p class="mt-1 truncate text-slate-800" [title]="step.requestTitle">{{ step.requestTitle }}</p>
                <p class="mt-1 truncate text-sm text-slate-600">{{ requesterOf(step) }}</p>
                <p class="mt-1 text-sm whitespace-nowrap text-slate-600">
                  @if (waitingOf(step)) { Waiting {{ waitingOf(step) }} }
                  @if (waitingOf(step) && escalationOf(step)) { · }
                  @if (escalationOf(step)) { Escalates {{ escalationOf(step) }} }
                </p>
                <div class="mt-3"><ng-container [ngTemplateOutlet]="actions" [ngTemplateOutletContext]="{ $implicit: step }" /></div>
                @if (store.errorOf(step.id) || rejecting() === step.id) {
                  <div class="mt-3"><ng-container [ngTemplateOutlet]="extra" [ngTemplateOutletContext]="{ $implicit: step, area: 'card' }" /></div>
                }
              </li>
            }
          </ul>
        </div>

        <app-pager [page]="store.page()" [pageCount]="store.pageCount()" [total]="store.total()" [loading]="store.isLoading()" noun="approval" nounPlural="approvals" (pageChange)="store.load($event)" />
      }
    </section>

    <ng-template #actions let-step>
      <div class="flex flex-wrap justify-end gap-2 lg:flex-nowrap">
        <span class="contents" data-action="approve">
          <app-busy-button
            [busy]="store.actingOn(step.id) === 'approve'"
            [disabled]="store.actingOn(step.id) !== null"
            busyLabel="Approving…"
            (pressed)="approve(step)"
            >Approve</app-busy-button
          >
        </span>
        <span class="contents" data-action="reject">
          <app-busy-button
            variant="secondary"
            [busy]="store.actingOn(step.id) === 'reject'"
            [disabled]="store.actingOn(step.id) !== null"
            busyLabel="Rejecting…"
            (pressed)="startReject(step)"
            >Reject</app-busy-button
          >
        </span>
      </div>
    </ng-template>

    <ng-template #extra let-step let-area="area">
      @if (store.errorOf(step.id); as message) {
        <p role="alert" class="rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-900">{{ message }}</p>
      }
      @if (rejecting() === step.id) {
        <div class="mt-3">
          <label class="field-label" [for]="'reason-' + area + '-' + step.id">Reason for rejecting {{ step.requestNumber }}</label>
          <textarea [id]="'reason-' + area + '-' + step.id" rows="2" class="field-input" [formField]="reasonForm.reason" placeholder="The requester will see this"></textarea>
          @for (error of reasonForm.reason().errors(); track error.kind) {
            <p class="field-error">{{ error.message }}</p>
          }
          <div class="mt-3 flex flex-wrap justify-end gap-2">
            <button type="button" class="btn-ghost" (click)="cancelReject()" [disabled]="store.actingOn(step.id) !== null">Cancel</button>
            <app-busy-button
              data-action="confirm-reject"
              [busy]="store.actingOn(step.id) === 'reject'"
              [disabled]="store.actingOn(step.id) !== null"
              busyLabel="Rejecting…"
              (pressed)="confirmReject(step)"
              >Reject request</app-busy-button
            >
          </div>
        </div>
      }
    </ng-template>
  `,
})
export class ApprovalsPage implements OnInit {
  protected readonly store = inject(ApprovalsStore);
  protected readonly skeletonRows = [1, 2, 3];
  /** The step whose reject box is open (one at a time). */
  protected readonly rejecting = signal<string | null>(null);
  private readonly reasonModel = signal({ reason: '' });
  protected readonly reasonForm = form(this.reasonModel, (p) => {
    maxLength(p.reason, MAX_REASON, { message: `Use at most ${MAX_REASON} characters` });
  });
  private readonly now = signal(Date.now());

  constructor() {
    const timer = setInterval(() => this.now.set(Date.now()), 60_000);
    inject(DestroyRef).onDestroy(() => clearInterval(timer));
  }

  ngOnInit(): void {
    void this.store.load(1);
  }

  protected requesterOf(step: ApprovalStepDto): string {
    return this.store.detailsOf(step.id)?.requester ?? '';
  }

  protected waitingOf(step: ApprovalStepDto): string {
    return waitedFor(this.store.detailsOf(step.id)?.waitingSince, this.now());
  }

  protected escalationOf(step: ApprovalStepDto): string {
    return escalatesIn(step.dueAt, this.now());
  }

  protected approve(step: ApprovalStepDto): void {
    void this.store.approve(step);
  }

  protected startReject(step: ApprovalStepDto): void {
    this.store.clearRowError(step.id);
    this.reasonModel.set({ reason: '' });
    this.rejecting.set(step.id);
  }

  protected cancelReject(): void {
    const id = this.rejecting();
    if (id) {
      this.store.clearRowError(id);
    }
    this.rejecting.set(null);
  }

  protected async confirmReject(step: ApprovalStepDto): Promise<void> {
    if (await this.store.reject(step, this.reasonModel().reason)) {
      this.rejecting.set(null);
    }
  }
}
