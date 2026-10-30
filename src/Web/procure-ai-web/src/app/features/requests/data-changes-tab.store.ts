import { computed, inject } from '@angular/core';
import { HttpContext, HttpErrorResponse } from '@angular/common/http';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import { UserDto } from '../../api/access/models/user-dto';
import { AccessService } from '../../api/access/services/access.service';
import { DataChangeDto } from '../../api/audit/models/data-change-dto';
import { DataChangesService } from '../../api/audit/services/data-changes.service';
import { AuthStore } from '../../core/auth/auth.store';
import { AppConfigService } from '../../core/config/app-config';
import { SKIP_FORBIDDEN_REDIRECT } from '../../core/http/errors.interceptor';
import { Logger } from '../../core/logging/logger';
import { withRequestStatus } from '../../core/state/with-request-status';

interface DataChangesTabState {
  /** The request the rows belong to; null until the tab was opened for the first time. */
  requestId: string | null;
  items: DataChangeDto[];
  page: number;
  total: number;
  users: UserDto[];
}

/**
 * The data changes of one request (the "Data changes" tab): loaded the first time the tab opens, then kept while the page lives
 * (switching back and forth does not ask again). Oldest change first, as the API lists them. Provide it in the page's `providers`.
 */
export const DataChangesTabStore = signalStore(
  withState<DataChangesTabState>({ requestId: null, items: [], page: 1, total: 0, users: [] }),
  withRequestStatus(),
  withComputed((store) => {
    const config = inject(AppConfigService);
    return {
      pageCount: computed(() => Math.max(1, Math.ceil(store.total() / config.config().paging.pageSize))),
      names: computed(() => new Map(store.users().map((u) => [u.userId, u.displayName]))),
    };
  }),
  withMethods((store) => {
    const api = inject(DataChangesService);
    const access = inject(AccessService);
    const auth = inject(AuthStore);
    const config = inject(AppConfigService);
    const log = inject(Logger).withSource('DataChangesTabStore');
    let latest = 0;

    const fetchPage = async (requestId: string, page: number): Promise<void> => {
      const call = ++latest;
      store.setLoading();
      try {
        const result = await api.getRequestDataChanges(
          { requestId, page, pageSize: config.config().paging.pageSize },
          new HttpContext().set(SKIP_FORBIDDEN_REDIRECT, true),
        );
        if (call === latest) {
          patchState(store, { items: result.items, page: result.page, total: result.total });
          store.setLoaded();
        }
      } catch (error) {
        if (call !== latest) {
          return;
        }
        if (error instanceof HttpErrorResponse && error.status === 404) {
          // The Audit service answers 404 when it has recorded no change for the request (yet).
          patchState(store, { items: [], page: 1, total: 0 });
          store.setLoaded();
          return;
        }
        log.error('Loading the data changes failed', error);
        store.setError(error, 'The data changes could not be loaded.');
      }
    };

    const loadNames = async (): Promise<void> => {
      // Only people who may read the user list get names; for everyone else the user IDs show.
      if (!auth.hasPermission('admin.users')) {
        return;
      }
      try {
        patchState(store, { users: await access.listUsers() });
      } catch (error) {
        log.warn('Reading the users for names failed', error);
      }
    };

    return {
      /** The tab was opened: loads the first page, once per request. */
      async open(requestId: string): Promise<void> {
        if (store.requestId() === requestId) {
          return;
        }
        patchState(store, { requestId, items: [], page: 1, total: 0 });
        await Promise.all([fetchPage(requestId, 1), loadNames()]);
      },

      async goToPage(page: number): Promise<void> {
        const requestId = store.requestId();
        if (requestId) {
          await fetchPage(requestId, page);
        }
      },

      async retry(): Promise<void> {
        const requestId = store.requestId();
        if (requestId) {
          await fetchPage(requestId, store.page());
        }
      },
    };
  }),
);
