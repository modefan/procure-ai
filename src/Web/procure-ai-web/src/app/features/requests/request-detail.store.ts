import { HttpErrorResponse } from '@angular/common/http';
import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withHooks, withMethods, withState } from '@ngrx/signals';
import { ApprovalChainDto } from '../../api/approvals/models/approval-chain-dto';
import { ApprovalsService } from '../../api/approvals/services/approvals.service';
import { BusinessEventDto } from '../../api/audit/models/business-event-dto';
import { AuditService } from '../../api/audit/services/audit.service';
import { InvoiceDto } from '../../api/invoicing/models/invoice-dto';
import { InvoicesService } from '../../api/invoicing/services/invoices.service';
import { PurchaseOrderDto } from '../../api/purchasing/models/purchase-order-dto';
import { OrdersService } from '../../api/purchasing/services/orders.service';
import { RequestDto } from '../../api/requests/models/request-dto';
import { RequestsService } from '../../api/requests/services/requests.service';
import { AuthStore } from '../../core/auth/auth.store';
import { EntityChanged } from '../../core/live/live.models';
import { LiveConnection } from '../../core/live/live-connection';
import { Logger } from '../../core/logging/logger';
import { CacheRegistry } from '../../core/state/cache-registry';
import { withRequestStatus } from '../../core/state/with-request-status';
import { ConflictDialog } from '../../core/ui/conflict-dialog';
import { problemOf } from './problem-fields';
import { buildTimeline } from './workflow-timeline';

interface DetailState {
  requestId: string | null;
  request: RequestDto | null;
  chain: ApprovalChainDto | null;
  order: PurchaseOrderDto | null;
  invoices: InvoiceDto[];
  history: BusinessEventDto[];
  /** Parts (not the request itself) that could not be loaded, in words for the page. */
  partial: string[];
  busy: boolean;
  /** A refusal of the cancel that belongs to no dialog (409, 422 ...). */
  generalError: string | null;
}

const EMPTY: DetailState = { requestId: null, request: null, chain: null, order: null, invoices: [], history: [], partial: [], busy: false, generalError: null };
/** Statuses a request can still be cancelled from (the server decides about the rest). */
const CANCELLABLE = ['Draft', 'Submitted', 'Approved', 'Ordered'];
const isNotFound = (error: unknown) => error instanceof HttpErrorResponse && error.status === 404;

/**
 * One request's detail: the request, its approval chain, order, invoices and business history, loaded in parallel
 * (order only with `orders.read`, invoices only with `invoices.read`). Provide it in the page's `providers`.
 *
 * Revalidates in the background whenever the hub says this request, one of its approvals, its order or an invoice
 * changed, and after a reconnect; a burst of pushes becomes one more load. Writes (cancel) wait for the server, send
 * `If-Match` with the loaded version and one `Idempotency-Key` per version, and answer a 412 with the conflict dialog.
 */
export const RequestDetailStore = signalStore(
  withState<DetailState>(EMPTY),
  withRequestStatus(),
  withComputed((store) => {
    const auth = inject(AuthStore);
    const canSeeOrder = computed(() => auth.hasPermission('orders.read'));
    const canSeeInvoices = computed(() => canSeeOrder() && auth.hasPermission('invoices.read'));
    /** Who raised the request: the approval chain and the history know; a draft has neither (the server then decides). */
    const ownerId = computed(() => store.chain()?.steps[0]?.requesterId ?? store.history().find((e) => e.type === 'RequestSubmitted')?.userId ?? null);
    const mine = computed(() => ownerId() === null || ownerId() === auth.user()?.userId);
    return {
      canSeeOrder,
      canSeeInvoices,
      ownerId,
      partialFailures: computed(() => store.partial()),
      canEdit: computed(() => store.request()?.status === 'Draft' && auth.hasPermission('requests.create') && mine()),
      canCancel: computed(() => {
        const request = store.request();
        const received = (store.order()?.receipts.length ?? 0) > 0;
        return !!request && CANCELLABLE.includes(request.status) && !received && auth.hasPermission('requests.create') && mine();
      }),
      timeline: computed(() => {
        const request = store.request();
        return request
          ? buildTimeline({
              request,
              chain: store.chain(),
              order: store.order(),
              invoices: store.invoices(),
              history: store.history(),
              canSeeOrder: canSeeOrder(),
              canSeeInvoices: canSeeInvoices(),
            })
          : [];
      }),
    };
  }),
  withMethods((store) => {
    const requests = inject(RequestsService);
    const approvals = inject(ApprovalsService);
    const orders = inject(OrdersService);
    const invoicing = inject(InvoicesService);
    const audit = inject(AuditService);
    const conflict = inject(ConflictDialog);
    const log = inject(Logger).withSource('RequestDetailStore');

    /** Changes at logout, so an answer that was on its way for the old user is dropped. */
    let generation = 0;
    let running: Promise<void> | null = null;
    let again = false;
    let seed: RequestDto | null = null;
    let cancelling: Promise<void> | null = null;
    const cancelKeys = new Map<string, string>();

    const loadAll = async (background: boolean): Promise<void> => {
      const id = store.requestId();
      if (!id) {
        return;
      }
      const mine = generation;
      if (!background) {
        store.setLoading();
      }
      const fromSeed = seed;
      seed = null;
      const requestCall = fromSeed ? Promise.resolve(fromSeed) : requests.getRequest({ id });
      const chainCall = approvals.getApprovalChain({ requestId: id }).catch((e: unknown) => (isNotFound(e) ? null : Promise.reject(e)));
      const orderCall: Promise<PurchaseOrderDto | null> = store.canSeeOrder()
        ? orders.getOrderForRequest({ requestId: id }).catch((e: unknown) => (isNotFound(e) ? null : Promise.reject(e)))
        : Promise.resolve(null);
      const invoicesCall: Promise<InvoiceDto[]> = orderCall.then(async (order) =>
        order && store.canSeeInvoices() ? (await invoicing.listInvoices({ orderId: order.id, page: 1, pageSize: 50 })).items : [],
      );
      const historyCall = audit.getRequestHistory({ requestId: id });

      const [request, chain, order, invoices, history] = await Promise.allSettled([requestCall, chainCall, orderCall, invoicesCall, historyCall]);
      if (mine !== generation || store.requestId() !== id) {
        return;
      }

      if (request.status === 'rejected') {
        if (background && store.request()) {
          log.warn('Refreshing the request failed; keeping what is shown', request.reason);
        } else {
          log.error('Loading the request failed', request.reason);
          store.setError(request.reason, 'The request could not be loaded.');
          return;
        }
      }
      const partial: string[] = [];
      const note = (name: string, result: PromiseSettledResult<unknown>) => {
        if (result.status === 'rejected') {
          log.warn(`Loading the ${name} failed`, result.reason);
          partial.push(name);
        }
      };
      note('approval steps', chain);
      note('order', order);
      note('invoices', invoices);
      note('audit trail', history);
      patchState(store, {
        ...(request.status === 'fulfilled' ? { request: request.value } : {}),
        ...(chain.status === 'fulfilled' ? { chain: chain.value } : {}),
        ...(order.status === 'fulfilled' ? { order: order.value } : {}),
        ...(invoices.status === 'fulfilled' ? { invoices: invoices.value } : {}),
        ...(history.status === 'fulfilled' ? { history: history.value } : {}),
        partial,
      });
      store.setLoaded();
    };

    /** Runs one load; a call while one runs asks for exactly one more load afterwards. */
    const reload = (background: boolean): Promise<void> => {
      if (running) {
        again = true;
        return running;
      }
      const call: Promise<void> = loadAll(background)
        .catch((error: unknown) => log.error('Loading the request failed unexpectedly', error))
        .finally(() => {
          if (running !== call) {
            return; // init() or logout started over meanwhile
          }
          running = null;
          if (again) {
            again = false;
            void reload(true);
          }
        });
      running = call;
      return call;
    };

    const keyFor = (id: string, version: number): string => {
      const k = `${id}:${version}`;
      let key = cancelKeys.get(k);
      if (!key) {
        key = crypto.randomUUID();
        cancelKeys.set(k, key);
      }
      return key;
    };

    return {
      /** Shows request `id`. `initial` is what the route resolver already fetched, so it is not asked for twice. */
      async init(id: string, initial: RequestDto | null): Promise<void> {
        generation++;
        running = null;
        again = false;
        cancelling = null;
        seed = initial;
        patchState(store, { ...EMPTY, requestId: id, request: initial });
        await reload(false);
      },

      /** Loads again, keeping what is shown (retry button, or after a conflict). */
      reload: () => reload(true),

      /** Cancels the request, after the page has asked the user. */
      cancel(): Promise<void> {
        const request = store.request();
        if (!request) {
          return Promise.resolve();
        }
        if (cancelling) {
          return cancelling; // a double click shares the running call
        }
        patchState(store, { busy: true, generalError: null });
        const key = keyFor(request.id, request.version);
        cancelling = (async () => {
          try {
            const updated = await requests.cancelRequest({ id: request.id, 'If-Match': `"${request.version}"`, 'Idempotency-Key': key });
            patchState(store, { request: updated });
            void reload(true);
          } catch (error) {
            const problem = problemOf(error);
            if (error instanceof HttpErrorResponse && error.status === 412) {
              await conflict.show(problem.general ?? 'This request was changed by someone else. The latest version is loaded.');
              await reload(true);
            } else {
              log.warn('Cancelling the request failed', error);
              patchState(store, { generalError: problem.general ?? 'The request could not be cancelled. Please try again.' });
            }
          } finally {
            cancelling = null;
            patchState(store, { busy: false });
          }
        })();
        return cancelling;
      },

      /** Logout: forget everything and drop answers still on their way. */
      clearCache(): void {
        generation++;
        running = null;
        seed = null;
        patchState(store, EMPTY);
      },

      /** A push or a reconnect: refresh quietly. */
      onChange: (change: EntityChanged): void => {
        const id = store.requestId();
        if (id && (change.requestId === id || change.id === id)) {
          void reload(true);
        }
      },
    };
  }),
  withHooks((store) => {
    const live = inject(LiveConnection);
    let release: (() => void)[] = [];
    return {
      onInit() {
        release = [
          inject(CacheRegistry).register(store),
          ...['request', 'approval', 'order', 'invoice'].map((entity) => live.onEntityChanged(entity, (change) => store.onChange(change))),
          live.onReconnected(() => void store.reload()),
        ];
      },
      onDestroy() {
        release.forEach((off) => off());
      },
    };
  }),
);
