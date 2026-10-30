import { HttpContext, HttpErrorResponse } from '@angular/common/http';
import { computed, inject } from '@angular/core';
import { signalStore, withComputed, withMethods } from '@ngrx/signals';
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
import { Logger } from '../../core/logging/logger';
import { SKIP_FORBIDDEN_REDIRECT } from '../../core/http/errors.interceptor';
import { problemOf } from '../../core/http/problem-fields';
import { versionEtag, withDetail } from '../../core/state/with-detail';
import { buildTimeline } from './workflow-timeline';

/** What the page shows: the request and the parts loaded around it. */
interface RequestDetail {
  request: RequestDto;
  chain: ApprovalChainDto | null;
  order: PurchaseOrderDto | null;
  invoices: InvoiceDto[];
  history: BusinessEventDto[];
}

const NO_PARTS: Omit<RequestDetail, 'request'> = { chain: null, order: null, invoices: [], history: [] };
/** Statuses a request can still be cancelled from (the server decides about the rest). */
const CANCELLABLE = ['Draft', 'Submitted', 'Approved', 'Ordered'];
/** Calls whose 403 must not take the page away: the user may simply not be allowed to read that part. */
const quietly = () => new HttpContext().set(SKIP_FORBIDDEN_REDIRECT, true);
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
  withDetail<RequestDetail, RequestDto>(() => {
    const requests = inject(RequestsService);
    const approvals = inject(ApprovalsService);
    const orders = inject(OrdersService);
    const invoicing = inject(InvoicesService);
    const audit = inject(AuditService);
    const auth = inject(AuthStore);
    const log = inject(Logger).withSource('RequestDetailStore');
    return {
      // The audit trail and the order are written by other services from the same events, a moment after the push that told us
      // about them: a push is followed by one more quiet load (`cache.trailingReloadMs`).
      entity: ['request', 'approval', 'order', 'invoice'],
      errorMessage: 'The request could not be loaded.',
      isRelevant: (change, id) => change.requestId === id || change.id === id,
      fromSeed: (request) => ({ value: { ...NO_PARTS, request }, etag: versionEtag(request) }),
      load: async (requestId, { background, current, seed }) => {
        const canSeeOrder = auth.hasPermission('orders.read');
        const canSeeInvoices = canSeeOrder && auth.hasPermission('invoices.read');
        const requestCall = seed ? Promise.resolve(seed) : requests.getRequest({ id: requestId });
        const chainCall = approvals.getApprovalChain({ requestId }, quietly()).catch((e: unknown) => (isNotFound(e) ? null : Promise.reject(e)));
        const orderCall: Promise<PurchaseOrderDto | null> = canSeeOrder
          ? orders.getOrderForRequest({ requestId }, quietly()).catch((e: unknown) => (isNotFound(e) ? null : Promise.reject(e)))
          : Promise.resolve(null);
        const invoicesCall: Promise<InvoiceDto[]> = orderCall.then(async (order) =>
          order && canSeeInvoices ? (await invoicing.listInvoices({ orderId: order.id, page: 1, pageSize: 50 }, quietly())).items : [],
        );
        const historyCall: Promise<BusinessEventDto[]> = audit.getRequestHistory({ requestId }, quietly()).catch((e: unknown) => (isNotFound(e) ? [] : Promise.reject(e)));

        const [request, chain, order, invoices, history] = await Promise.allSettled([requestCall, chainCall, orderCall, invoicesCall, historyCall]);
        if (request.status === 'rejected') {
          if (!(background && current)) {
            throw request.reason; // nothing to show
          }
          log.warn('Refreshing the request failed; keeping what is shown', request.reason);
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
        // The invoices are asked for only once the order is known: when the order failed, that is the one thing missing.
        if (order.status === 'fulfilled') {
          note('invoices', invoices);
        }
        note('audit trail', history);
        // A part that could not be loaded keeps what was shown before.
        const value: RequestDetail = {
          request: request.status === 'fulfilled' ? request.value : current!.request,
          chain: chain.status === 'fulfilled' ? chain.value : (current?.chain ?? null),
          order: order.status === 'fulfilled' ? order.value : (current?.order ?? null),
          invoices: invoices.status === 'fulfilled' ? invoices.value : (current?.invoices ?? []),
          history: history.status === 'fulfilled' ? history.value : (current?.history ?? []),
        };
        return { value, etag: versionEtag(value.request), partial };
      },
    };
  }),
  withComputed((store) => ({
    requestId: store.id,
    request: computed(() => store.value()?.request ?? null),
    chain: computed(() => store.value()?.chain ?? null),
    order: computed(() => store.value()?.order ?? null),
    invoices: computed(() => store.value()?.invoices ?? []),
    history: computed(() => store.value()?.history ?? []),
    busy: computed(() => store.busyKind() !== null),
    partialFailures: store.partial,
  })),
  withComputed((store) => {
    const auth = inject(AuthStore);
    const canSeeOrder = computed(() => auth.hasPermission('orders.read'));
    const canSeeInvoices = computed(() => canSeeOrder() && auth.hasPermission('invoices.read'));
    /** Only the requester may edit or cancel; the server checks too (403). */
    const mine = computed(() => store.request() !== null && store.request()!.requesterId === auth.user()?.userId);
    return {
      canSeeOrder,
      canSeeInvoices,
      /** "you", the requester's name when Audit knows it (their submit event), else null (a draft has none). */
      requestedBy: computed(() => (mine() ? 'you' : (store.history().find((e) => e.type === 'RequestSubmitted')?.actorName ?? null))),
      canEdit: computed(() => store.request()?.status === 'Draft' && auth.hasPermission('requests.create') && mine()),
      canCancel: computed(() => {
        const request = store.request();
        const received = (store.order()?.receipts.length ?? 0) > 0;
        // Whether goods were received is only visible with orders.read; without it the cancel could be refused, so it is not offered.
        // A draft has no order, so it needs no look.
        const knowsReceipts = canSeeOrder() || request?.status === 'Draft';
        return !!request && CANCELLABLE.includes(request.status) && knowsReceipts && !received && auth.hasPermission('requests.create') && mine();
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

    return {
      /** Shows request `id`. `initial` is what the route resolver already fetched, so it is not asked for twice. */
      init: (id: string, initial: RequestDto | null): Promise<void> => store.open(id, initial),

      /** Cancels the request, after the page has asked the user. `reason` is what the user typed (may be empty). */
      async cancel(reason = ''): Promise<void> {
        const request = store.request();
        if (!request) {
          return;
        }
        await store.runWrite((etag, key) => requests.cancelRequest({ id: request.id, 'If-Match': etag ?? versionEtag(request), 'Idempotency-Key': key, body: { reason: reason.trim() || null } }, quietly()), {
          what: 'Cancelling the request',
          failureMessage: 'The request could not be cancelled. Please try again.',
          conflictMessage: (error) => problemOf(error).general ?? 'This request was changed by someone else. The latest version is loaded.',
          keyParts: [request.id, request.version, reason.trim()],
          apply: (updated, current) => ({ value: { ...(current ?? NO_PARTS), request: updated }, etag: versionEtag(updated) }),
          reloadAfter: true,
        });
      },
    };
  }),
);
