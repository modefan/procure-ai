import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withHooks, withMethods, withState } from '@ngrx/signals';
import { BellItem } from '../../api/notifications/models/bell-item';
import { NotificationsService } from '../../api/notifications/services/notifications.service';
import { Logger } from '../logging/logger';
import { withCache } from '../state/with-cache';
import { withRequestStatus } from '../state/with-request-status';
import { LiveConnection } from './live-connection';

interface NotificationsState {
  /** Newest first. */
  items: BellItem[];
}

/**
 * The bell: the user's latest notifications (the server keeps 50, 30 days) and how many are unread.
 * Live through `NotificationAdded`; the same item can arrive twice, so items are de-duplicated by id. Marking read is
 * optimistic (a harmless action) and rolled back when the server refuses. Reloaded in the background on every
 * reconnect (CacheRegistry) and emptied at logout.
 */
export const NotificationsStore = signalStore(
  { providedIn: 'root' },
  withState<NotificationsState>({ items: [] }),
  withRequestStatus(),
  withComputed(({ items }) => ({
    unread: computed(() => items().filter((i) => !i.read).length),
  })),
  withCache(),
  withMethods((store) => {
    const api = inject(NotificationsService);
    const log = inject(Logger).withSource('NotificationsStore');

    return {
      /** Shows the list (once fresh, a second call does nothing). A failure sets `error` and keeps what is shown. */
      async load(): Promise<void> {
        const epoch = store.cacheEpoch();
        try {
          await store.cacheLoad('list', async () => {
            if (store.items().length === 0) {
              store.setLoading();
            }
            try {
              const list = await api.listNotifications();
              if (epoch !== store.cacheEpoch()) {
                return; // logged out meanwhile: the answer belongs to the old user
              }
              patchState(store, { items: list.items });
              store.setLoaded();
            } catch (error) {
              if (epoch === store.cacheEpoch()) {
                log.warn('Loading the notifications failed', error);
                store.setError(error, 'The notifications could not be loaded.');
              }
              throw error;
            }
          });
        } catch {
          // Recorded in `error`.
        }
      },

      /** Adds a pushed item at the top unless it is already there. */
      add(item: BellItem): void {
        if (store.items().some((i) => i.id === item.id)) {
          return;
        }
        patchState(store, (s) => ({ items: [item, ...s.items] }));
      },

      /** Marks one item read at once; puts it back as unread if the server refuses. */
      async markRead(id: string): Promise<void> {
        const target = store.items().find((i) => i.id === id);
        if (!target || target.read) {
          return;
        }
        patchState(store, (s) => ({ items: s.items.map((i) => (i.id === id ? { ...i, read: true } : i)) }));
        try {
          await api.markNotificationRead({ id, 'Idempotency-Key': crypto.randomUUID() });
        } catch (error) {
          log.warn('Marking a notification read failed', error);
          patchState(store, (s) => ({ items: s.items.map((i) => (i.id === id ? { ...i, read: false } : i)) }));
        }
      },

      /** Marks everything read at once; restores exactly the items that were unread if the server refuses. */
      async markAllRead(): Promise<void> {
        const unreadIds = new Set(store.items().filter((i) => !i.read).map((i) => i.id));
        if (unreadIds.size === 0) {
          return;
        }
        patchState(store, (s) => ({ items: s.items.map((i) => ({ ...i, read: true })) }));
        try {
          await api.markAllNotificationsRead({ 'Idempotency-Key': crypto.randomUUID() });
        } catch (error) {
          log.warn('Marking all notifications read failed', error);
          patchState(store, (s) => ({ items: s.items.map((i) => (unreadIds.has(i.id) ? { ...i, read: false } : i)) }));
        }
      },
    };
  }),
  withHooks((store) => {
    let off: (() => void) | null = null;
    return {
      onInit() {
        off = inject(LiveConnection).on<[BellItem]>('NotificationAdded', (item) => store.add(item));
      },
      onDestroy() {
        off?.();
      },
    };
  }),
);
