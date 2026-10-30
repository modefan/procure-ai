import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import { AdminConversationDto } from '../../../api/assistant/models/admin-conversation-dto';
import { AdminConversationListItem } from '../../../api/assistant/models/admin-conversation-list-item';
import { AdminConversationsService } from '../../../api/assistant/services/admin-conversations.service';
import { UserDto } from '../../../api/access/models/user-dto';
import { AccessService } from '../../../api/access/services/access.service';
import { AppConfigService } from '../../../core/config/app-config';
import { Logger } from '../../../core/logging/logger';
import { withCache } from '../../../core/state/with-cache';
import { withRequestStatus } from '../../../core/state/with-request-status';

interface ConversationsState {
  items: AdminConversationListItem[];
  page: number;
  total: number;
  /** Only this user's conversations (their id); empty = everyone's. */
  userId: string;
  detail: AdminConversationDto | null;
  /** The users of the tenant, for names and the filter picker; empty when Access could not be asked (ids are shown then). */
  users: UserDto[];
}

/** The read-only admin view of assistant conversations (`admin.system`): a paged list with a user filter, and one conversation. */
export const ConversationsStore = signalStore(
  { providedIn: 'root' },
  withState<ConversationsState>({ items: [], page: 1, total: 0, userId: '', detail: null, users: [] }),
  withRequestStatus(),
  withCache(),
  withComputed((store) => {
    const config = inject(AppConfigService);
    return {
      pageCount: computed(() => Math.max(1, Math.ceil(store.total() / config.config().paging.pageSize))),
      /** Answers the guard blocked in the open conversation. */
      /** user id to display name. */
      names: computed(() => new Map(store.users().map((u) => [u.userId, u.displayName]))),
      blockedCount: computed(() => store.detail()?.messages.filter((m) => m.status === 'Blocked').length ?? 0),
    };
  }),
  withMethods((store) => {
    const api = inject(AdminConversationsService);
    const config = inject(AppConfigService);
    const access = inject(AccessService);
    const log = inject(Logger).withSource('ConversationsStore');
    let latest = 0;

    const fetchList = async (): Promise<void> => {
      const call = ++latest;
      const epoch = store.cacheEpoch();
      store.setLoading();
      try {
        const result = await api.adminListConversations({
          userId: store.userId() || undefined,
          page: store.page(),
          pageSize: config.config().paging.pageSize,
        });
        if (call !== latest || epoch !== store.cacheEpoch()) {
          return;
        }
        patchState(store, { items: result.items, total: result.total });
        store.setLoaded();
      } catch (error) {
        if (call === latest && epoch === store.cacheEpoch()) {
          log.error('Loading conversations failed', error);
          store.setError(error, 'The conversations could not be loaded.');
        }
      }
    };

    let usersRequested = false;

    return {
      load: fetchList,

      /** Reads the users once per session for names; a failure leaves the ids. */
      async loadUsers(): Promise<void> {
        if (usersRequested || store.users().length > 0) {
          return;
        }
        usersRequested = true;
        const epoch = store.cacheEpoch();
        try {
          const users = await access.listUsers();
          if (epoch === store.cacheEpoch()) {
            patchState(store, { users });
          }
        } catch (error) {
          log.warn('Reading the users for names failed', error);
        } finally {
          usersRequested = false;
        }
      },

      async setUser(userId: string): Promise<void> {
        patchState(store, { userId: userId.trim(), page: 1 });
        await fetchList();
      },

      async goToPage(page: number): Promise<void> {
        patchState(store, { page });
        await fetchList();
      },

      async loadDetail(id: string): Promise<void> {
        const epoch = store.cacheEpoch();
        patchState(store, { detail: null });
        store.setLoading();
        try {
          const detail = await api.adminGetConversation({ id });
          if (epoch !== store.cacheEpoch()) {
            return;
          }
          patchState(store, { detail });
          store.setLoaded();
        } catch (error) {
          if (epoch === store.cacheEpoch()) {
            log.error('Loading a conversation failed', error);
            store.setError(error, 'The conversation could not be loaded.');
          }
        }
      },
    };
  }),
);

export type ConversationsStore = InstanceType<typeof ConversationsStore>;
