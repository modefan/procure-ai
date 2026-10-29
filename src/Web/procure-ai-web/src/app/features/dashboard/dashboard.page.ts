import { DatePipe } from '@angular/common';
import { Component, computed, DestroyRef, inject, OnInit, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AuthStore } from '../../core/auth/auth.store';
import { PresenceStore } from '../../core/live/presence.store';
import { KeepRandPipe, RandPipe } from '../../core/ui/money.pipe';
import { escalatesIn } from '../approvals/inbox-time';
import { DashboardStore } from './dashboard.store';

const NAMES_SHOWN = 3;

/**
 * Screen 4: the dashboard. Tiles exist only for the permissions the user holds (waiting approvals, my open requests,
 * budget per cost center, invoices with a mismatch); recent activity and who is online sit below. Everything is
 * live: the store refreshes a tile when the hub says its data changed.
 */
@Component({
  selector: 'app-dashboard-page',
  imports: [RouterLink, DatePipe, RandPipe, KeepRandPipe],
  template: `
    <section class="mx-auto max-w-6xl px-4 py-8">
      <h1 class="text-2xl font-bold tracking-tight text-slate-900">Dashboard</h1>
      @if (auth.user(); as user) {
        <p class="mt-1 text-slate-600">Welcome back, {{ user.name }}.</p>
      }

      <div class="mt-6 grid gap-4 sm:grid-cols-2 lg:grid-cols-[repeat(auto-fit,minmax(15rem,1fr))]">
        @if (store.tiles().approvals) {
          <div data-tile="approvals" class="card flex flex-col gap-1">
            <a routerLink="/app/approvals" class="text-sm text-slate-600 hover:underline">Waiting for my approval</a>
            @if (store.approvalsError()) {
              <span class="text-sm text-red-800">Could not be loaded.</span>
              <button type="button" class="btn-secondary self-start" (click)="store.retry('approvals')">Try again</button>
            } @else {
              <span class="text-4xl font-bold tabular-nums text-slate-900">{{ store.approvalsTotal() }}</span>
              @if (escalation(); as soon) {
                <span class="badge-warn">{{ soon }}</span>
              } @else {
                <span class="text-sm text-slate-500">&nbsp;</span>
              }
            }
          </div>
        }

        @if (store.tiles().myOpen) {
          <div data-tile="my-open" class="card flex flex-col gap-1">
            <a routerLink="/app/requests" class="text-sm text-slate-600 hover:underline">My open requests</a>
            @if (store.errors()['myOpen']) {
              <span class="text-sm text-red-800">Could not be loaded.</span>
              <button type="button" class="btn-secondary self-start" (click)="store.retry('myOpen')">Try again</button>
            } @else {
              <span class="text-4xl font-bold tabular-nums text-slate-900">{{ store.myOpen() ?? '–' }}</span>
            }
          </div>
        }

        @for (b of store.budgetTiles(); track b.code) {
          <div [attr.data-tile]="'budget-' + b.code" class="card flex flex-col gap-1">
            <span class="text-sm text-slate-600">{{ b.code }} budget used</span>
            <span class="text-4xl font-bold tabular-nums text-slate-900">{{ b.spentPercent }}%</span>
            <div class="flex h-2 overflow-hidden rounded-full bg-slate-200" role="img" [attr.aria-label]="b.code + ': ' + b.spentPercent + ' percent spent, ' + b.reservedPercent + ' percent reserved'">
              <div class="bar-spent" [style.--share]="b.spentPercent + '%'"></div>
              <div class="bar-reserved" [style.--share]="b.reservedPercent + '%'"></div>
            </div>
            <span class="text-sm text-slate-600"><span class="whitespace-nowrap">{{ b.spent | rand }}</span> of <span class="whitespace-nowrap">{{ b.amount | rand }}</span></span>
            <span class="text-sm text-slate-600"><span class="whitespace-nowrap">{{ b.reserved | rand }}</span> reserved</span>
          </div>
        }
        @if (store.tiles().budgets && store.errors()['budgets']) {
          <div data-tile="budgets-error" class="card flex flex-col gap-1">
            <span class="text-sm text-slate-600">Budget</span>
            <span class="text-sm text-red-800">Could not be loaded.</span>
            <button type="button" class="btn-secondary self-start" (click)="store.retry('budgets')">Try again</button>
          </div>
        }

        @if (store.tiles().mismatched) {
          <div data-tile="mismatched" class="card flex flex-col gap-1">
            <a routerLink="/app/invoices" class="text-sm text-slate-600 hover:underline">Invoices with a mismatch</a>
            @if (store.errors()['mismatched']) {
              <span class="text-sm text-red-800">Could not be loaded.</span>
              <button type="button" class="btn-secondary self-start" (click)="store.retry('mismatched')">Try again</button>
            } @else {
              <span class="text-4xl font-bold tabular-nums text-slate-900">{{ store.mismatched() ?? '–' }}</span>
            }
          </div>
        }
      </div>

      @if (store.tiles().activity) {
        <div class="mt-8 flex items-center gap-3">
          <h2 class="text-lg font-semibold text-slate-900">Recent activity</h2>
          <span class="badge-live">live</span>
        </div>
        @if (store.errors()['activity']) {
          <div role="alert" class="mt-3 flex flex-wrap items-center justify-between gap-3 rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-900">
            <span>Recent activity could not be loaded.</span>
            <button type="button" class="btn-secondary" (click)="store.retry('activity')">Try again</button>
          </div>
        } @else if (store.activity().length === 0) {
          <p class="card mt-3 text-slate-600">Nothing has happened yet.</p>
        } @else {
          <ul aria-label="Recent activity" class="card mt-3 divide-y divide-slate-100 p-0">
            @for (item of store.activity(); track item.occurredAt + item.eventType + item.requestId) {
              <li class="flex gap-4 px-4 py-3 text-sm">
                <time class="w-24 shrink-0 whitespace-nowrap text-slate-500" [attr.datetime]="item.occurredAt">{{ item.occurredAt | date: 'd MMM HH:mm' }}</time>
                <span class="min-w-0 text-slate-800">
                  @if (item.requestId) {
                    <a [routerLink]="['/app/requests', item.requestId]" class="break-words hover:underline">{{ item.summary | keepRand }}</a>
                  } @else {
                    <span class="break-words">{{ item.summary | keepRand }}</span>
                  }
                </span>
              </li>
            }
          </ul>
        }
      }

      <p class="mt-6 text-sm text-slate-600" aria-label="Online now">
        @if (online().names.length === 0) {
          Nobody else is online.
        } @else {
          Online now: {{ online().names.join(', ') }}@if (online().more > 0) { +{{ online().more }} }
        }
      </p>
    </section>
  `,
})
export class DashboardPage implements OnInit {
  protected readonly auth = inject(AuthStore);
  protected readonly store = inject(DashboardStore);
  private readonly presence = inject(PresenceStore);
  private readonly now = signal(Date.now());

  /** The first three names online (me excluded) and how many more there are. */
  protected readonly online = computed(() => {
    const names = this.presence.online().map((u) => u.name);
    return { names: names.slice(0, NAMES_SHOWN), more: Math.max(0, names.length - NAMES_SHOWN) };
  });

  /** "1 escalates in 6 h" under the approvals count: how many steps are due and when the first one is. */
  protected readonly escalation = computed(() => {
    const soonest = this.store.soonestEscalation();
    return soonest ? `Escalates ${escalatesIn(soonest, this.now())}` : null;
  });

  constructor() {
    const timer = setInterval(() => this.now.set(Date.now()), 60_000);
    inject(DestroyRef).onDestroy(() => clearInterval(timer));
  }

  ngOnInit(): void {
    void this.store.load();
  }

}
