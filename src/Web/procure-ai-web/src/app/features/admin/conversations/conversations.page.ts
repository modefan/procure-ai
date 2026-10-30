import { DatePipe } from '@angular/common';
import { Component, computed, inject, OnInit } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Pager } from '../../../core/ui/pager';
import { ConversationsStore } from './conversations.store';

/** Admin view of assistant conversations (needs `admin.system`): everyone's, newest first, filtered by user id. Read-only. */
@Component({
  selector: 'app-conversations-page',
  imports: [DatePipe, Pager, RouterLink],
  template: `
    <section class="mx-auto max-w-6xl px-4 py-8">
      <a routerLink="/app/admin/system" class="text-sm text-indigo-700 hover:underline">&lsaquo; System status</a>
      <div class="mt-2 flex flex-wrap items-end justify-between gap-4">
        <div>
          <h1 class="text-2xl font-bold tracking-tight text-slate-900">Assistant conversations</h1>
          <p class="mt-1 text-slate-600">Read-only. Pick a user to filter. Open one to see the tool steps, sources, tokens and guard flags.</p>
        </div>
        <div>
          <label for="conversation-user" class="field-label">User</label>
          <select id="conversation-user" class="field-input w-auto min-w-48" [value]="store.userId()" (change)="pick($event)">
            <option value="">Everyone</option>
            @for (user of pickList(); track user.id) {
              <option [value]="user.id">{{ user.name }}</option>
            }
          </select>
        </div>
      </div>

      @if (store.error(); as message) {
        <div role="alert" class="mt-6 flex flex-wrap items-center justify-between gap-3 rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-900">
          <span>{{ message }}</span>
          <button type="button" class="btn-secondary" (click)="store.load()">Try again</button>
        </div>
      }

      @if (store.isLoading() && store.items().length === 0) {
        <div class="card mt-6 space-y-4" role="status" aria-live="polite">
          <span class="sr-only">Loading conversations</span>
          @for (row of skeletonRows; track row) {
            <div class="h-6 animate-pulse rounded bg-slate-200" aria-hidden="true"></div>
          }
        </div>
      } @else if (!store.error()) {
        <ul class="mt-6 divide-y divide-slate-100 rounded-xl border border-slate-200 bg-white shadow-sm" data-testid="conversations">
          @for (item of store.items(); track item.id) {
            <li [attr.data-conversation]="item.id" class="flex flex-wrap items-baseline justify-between gap-x-4 gap-y-1 px-4 py-3">
              <a [routerLink]="['/app/admin/conversations', item.id]" class="min-w-0 flex-1 font-medium break-words text-indigo-700 hover:underline">{{ item.title }}</a>
              <span class="text-sm text-slate-700">{{ store.names().get(item.userId) ?? item.userId }}</span>
              <span class="text-xs whitespace-nowrap text-slate-600">{{ item.messageCount }} {{ item.messageCount === 1 ? 'message' : 'messages' }}</span>
              @if (item.blockedCount > 0) {
                <span class="badge-error">{{ item.blockedCount }} blocked</span>
              }
              <span class="text-xs whitespace-nowrap text-slate-500">{{ item.updatedAt | date: 'd MMM y, HH:mm' }}</span>
            </li>
          } @empty {
            <li class="px-4 py-6 text-center text-slate-600">No conversations.</li>
          }
        </ul>
        <app-pager [page]="store.page()" [pageCount]="store.pageCount()" [total]="store.total()" [loading]="store.isLoading()" noun="conversation" nounPlural="conversations" (pageChange)="store.goToPage($event)" />
      }
    </section>
  `,
})
export class ConversationsPage implements OnInit {
  protected readonly store = inject(ConversationsStore);
  /** The picker: every user Access knows, plus the filtered id when it is not among them. */
  protected readonly pickList = computed(() => {
    const names = this.store.names();
    const list = [...names].map(([id, name]) => ({ id, name }));
    const current = this.store.userId();
    if (current && !names.has(current)) {
      list.push({ id: current, name: current });
    }
    return list.sort((a, b) => a.name.localeCompare(b.name));
  });
  protected readonly skeletonRows = [1, 2, 3, 4];

  ngOnInit(): void {
    void this.store.load();
    void this.store.loadUsers();
  }

  protected pick(event: Event): void {
    void this.store.setUser((event.target as HTMLSelectElement).value);
  }
}
