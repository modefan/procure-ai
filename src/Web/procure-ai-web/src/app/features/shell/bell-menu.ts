import { Component, computed, inject } from '@angular/core';
import { Menu, MenuContent, MenuItem, MenuTrigger } from '@angular/aria/menu';
import { Router } from '@angular/router';
import { NotificationsStore } from '../../core/live/notifications.store';

/** The bell in the header: unread count (9+ at most), and a menu with the latest notifications. */
@Component({
  selector: 'app-bell-menu',
  imports: [Menu, MenuContent, MenuItem, MenuTrigger],
  template: `
    <div class="relative">
      <button
        ngMenuTrigger
        #trigger="ngMenuTrigger"
        [menu]="bellMenu"
        type="button"
        data-testid="bell"
        class="btn-ghost relative !px-2.5"
        [attr.aria-label]="ariaLabel()"
      >
        <svg aria-hidden="true" viewBox="0 0 24 24" class="size-5 fill-none stroke-current stroke-2">
          <path d="M6 8a6 6 0 1 1 12 0c0 7 3 9 3 9H3s3-2 3-9" stroke-linecap="round" stroke-linejoin="round" />
          <path d="M10.3 21a1.9 1.9 0 0 0 3.4 0" stroke-linecap="round" />
        </svg>
        @if (store.unread() > 0) {
          <span class="absolute -right-0.5 -top-0.5 min-w-4 rounded-full bg-red-600 px-1 text-center text-[10px] leading-4 font-bold text-white">{{ badge() }}</span>
        }
      </button>
      <div
        ngMenu
        #bellMenu="ngMenu"
        (itemSelected)="onSelect($event)"
        [class.hidden]="!trigger.expanded()"
        class="absolute right-0 z-40 mt-1 w-80 max-w-[calc(100vw-2rem)] rounded-lg border border-slate-200 bg-white py-1 shadow-lg"
        aria-label="Notifications"
      >
        <ng-template ngMenuContent>
          <div ngMenuItem value="read-all" [disabled]="store.unread() === 0" class="menu-item border-b border-slate-100 font-semibold text-indigo-700 aria-disabled:cursor-default aria-disabled:text-slate-400">
            Mark all as read
          </div>
          @for (item of store.items(); track item.id) {
            <div ngMenuItem [value]="item.id" class="menu-item flex items-start gap-2">
              <span aria-hidden="true" class="mt-1.5 size-2 shrink-0 rounded-full" [class]="item.read ? 'bg-transparent' : 'bg-indigo-600'"></span>
              <span class="min-w-0">
                <span class="block text-sm text-slate-800" [class.font-semibold]="!item.read">{{ item.text }}</span>
                <span class="block text-xs whitespace-nowrap text-slate-500">{{ when(item.createdAt) }}</span>
              </span>
            </div>
          } @empty {
            <div ngMenuItem value="none" [disabled]="true" class="menu-item text-sm text-slate-500">No notifications yet</div>
          }
        </ng-template>
      </div>
    </div>
  `,
})
export class BellMenu {
  protected readonly store = inject(NotificationsStore);
  private readonly router = inject(Router);

  protected readonly badge = computed(() => (this.store.unread() > 9 ? '9+' : String(this.store.unread())));
  protected readonly ariaLabel = computed(() => {
    const unread = this.store.unread();
    return unread > 0 ? `Notifications, ${unread} unread` : 'Notifications';
  });

  protected onSelect(value: string | undefined): void {
    if (value === 'read-all') {
      void this.store.markAllRead();
    } else if (value && value !== 'none') {
      const item = this.store.items().find((i) => i.id === value);
      if (item) {
        void this.store.markRead(item.id);
        void this.router.navigateByUrl(`/app${item.link}`);
      }
    }
  }

  protected when(iso: string): string {
    return new Intl.DateTimeFormat('en-ZA', { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(iso));
  }
}
