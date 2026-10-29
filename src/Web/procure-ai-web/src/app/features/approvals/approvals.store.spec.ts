import { HttpContext, HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { ApprovalsService } from '../../api/approvals/services/approvals.service';
import { SKIP_FORBIDDEN_REDIRECT } from '../../core/http/errors.interceptor';
import { fakeHubFactory } from '../../core/live/fake-hub.testing';
import { LIVE_HUB_FACTORY } from '../../core/live/hub-connection';
import { LiveConnection } from '../../core/live/live-connection';
import { CacheRegistry } from '../../core/state/cache-registry';
import { ConflictDialog } from '../../core/ui/conflict-dialog';
import { environment } from '../../../environments/environment';
import { inboxPage, inboxStep } from './approvals.testing';
import { ApprovalsStore } from './approvals.store';

describe('ApprovalsStore', () => {
  let api: Record<string, ReturnType<typeof vi.fn>>;
  let conflict: { show: ReturnType<typeof vi.fn> };
  let fake: ReturnType<typeof fakeHubFactory>;
  let live: LiveConnection;

  async function setup(items = [inboxStep(), inboxStep({ id: 's2', requestId: 'r2', requestNumber: 'PR-1045', version: 77 })]) {
    api = {
      getApprovalInbox: vi.fn().mockResolvedValue(inboxPage(items)),
      approveStep: vi.fn().mockResolvedValue(inboxStep({ status: 'Approved' })),
      rejectStep: vi.fn().mockResolvedValue(inboxStep({ status: 'Rejected' })),
    };
    conflict = { show: vi.fn().mockResolvedValue(undefined) };
    fake = fakeHubFactory();
    TestBed.configureTestingModule({
      providers: [
        { provide: ApprovalsService, useValue: { getApprovalInbox: api['getApprovalInbox'], approveStep: api['approveStep'], rejectStep: api['rejectStep'] } },
        { provide: ConflictDialog, useValue: conflict },
        { provide: LIVE_HUB_FACTORY, useValue: fake.factory },
      ],
    });
    live = TestBed.inject(LiveConnection);
    const store = TestBed.inject(ApprovalsStore);
    live.start();
    await vi.waitFor(() => expect(live.state()).toBe('connected'));
    return store;
  }

  afterEach(() => void live.stop());

  it('loads the inbox page and exposes the total for the header', async () => {
    const store = await setup();
    await store.load(1);
    expect(api['getApprovalInbox']).toHaveBeenCalledWith({ page: 1, pageSize: 25 });
    expect(store.entities().map((s) => s.requestNumber)).toEqual(['PR-1044', 'PR-1045']);
    expect(store.total()).toBe(2);
  });

  it('one_call_loads_the_inbox_with_names_and_times: no history reads per row', async () => {
    const store = await setup();
    await store.load(1);
    expect(store.entities()[0].requesterName).toBe('Ana Smith');
    expect(store.entities()[0].openedAt).toBeTruthy();
    expect(api['getApprovalInbox']).toHaveBeenCalledTimes(1);
  });

  it('soonest_due_is_kept_from_page_1', async () => {
    const store = await setup([inboxStep({ dueAt: '2026-10-30T09:00:00Z' }), inboxStep({ id: 's2', dueAt: '2026-10-29T09:00:00Z' })]);
    await store.load(1);
    expect(store.soonestDueAt()).toBe('2026-10-29T09:00:00Z');
    api['getApprovalInbox'].mockResolvedValue(inboxPage([inboxStep({ id: 's9', dueAt: '2026-11-30T09:00:00Z' })], 2, 26));
    await store.load(2);
    expect(store.soonestDueAt()).toBe('2026-10-29T09:00:00Z');
  });

  it('a_page_that_emptied_meanwhile_shows_the_last_page_with_rows', async () => {
    const store = await setup();
    await store.load(1);
    api['getApprovalInbox'].mockImplementation(({ page }: { page: number }) =>
      Promise.resolve(page === 2 ? inboxPage([], 2, 3) : inboxPage([inboxStep()], 1, 3)),
    );
    await store.load(2);
    await vi.waitFor(() => expect(store.page()).toBe(1));
    expect(store.entities()).toHaveLength(1);
  });

  it('approve_sends_if_match_and_removes_row_after_success', async () => {
    const store = await setup();
    await store.load(1);
    api['getApprovalInbox'].mockResolvedValue(inboxPage([inboxStep({ id: 's2', requestId: 'r2', requestNumber: 'PR-1045', version: 77 })]));

    const done = store.approve(store.entities()[0]);
    expect(store.actingOn('s1')).toBe('approve'); // busy while the server works
    await done;

    const call = api['approveStep'].mock.calls[0] as [{ stepId: string; 'If-Match': string; 'Idempotency-Key': string }, HttpContext];
    expect(call[0].stepId).toBe('s1');
    expect(call[0]['If-Match']).toBe('"1730"');
    expect(call[0]['Idempotency-Key']).toMatch(/^[0-9a-f-]{36}$/);
    expect(call[1].get(SKIP_FORBIDDEN_REDIRECT)).toBe(true);
    expect(store.entities().map((s) => s.id)).toEqual(['s2']);
    expect(store.total()).toBe(1);
    expect(store.actingOn('s1')).toBeNull();
  });

  it('a double click is one call', async () => {
    const store = await setup();
    await store.load(1);
    const step = store.entities()[0];
    await Promise.all([store.approve(step), store.approve(step)]);
    expect(api['approveStep']).toHaveBeenCalledTimes(1);
  });

  it('a retry after a failure repeats the idempotency key', async () => {
    const store = await setup();
    await store.load(1);
    const step = store.entities()[0];
    api['approveStep'].mockRejectedValueOnce(new HttpErrorResponse({ status: 503 }));
    await store.approve(step);
    await store.approve(step);
    const keys = api['approveStep'].mock.calls.map((c) => (c[0] as Record<string, string>)['Idempotency-Key']);
    expect(keys[0]).toBe(keys[1]);
  });

  it('conflict_412_shows_dialog_and_reloads', async () => {
    const store = await setup();
    await store.load(1);
    api['approveStep'].mockRejectedValue(new HttpErrorResponse({ status: 412, error: { detail: 'stale' } }));
    api['getApprovalInbox'].mockResolvedValue(inboxPage([inboxStep({ id: 's2', requestId: 'r2', requestNumber: 'PR-1045', version: 77 })]));

    await store.approve(store.entities()[0]);

    expect(conflict.show).toHaveBeenCalledWith('Someone else just changed PR-1044. Showing the latest version.');
    await vi.waitFor(() => expect(store.entities().map((s) => s.id)).toEqual(['s2']));
    expect(store.actingOn('s1')).toBeNull();
  });

  it('conflict_409_when_the_step_is_no_longer_open_is_the_same_story_as_412', async () => {
    // PINNED ASSUMPTION: the saga throws a ConflictException (409) when the step is no longer open and that check runs before
    // the version comparison (412), so "someone else got there first" arrives as 409. Approve and reject have no other 409; if
    // one is added, this spec and the store's status check must tell them apart (by problem title) first.
    const store = await setup();
    await store.load(1);
    api['approveStep'].mockRejectedValue(new HttpErrorResponse({
        status: 409,
        // The shape the API really sends: ConflictException becomes a 409 whose title is the message.
        error: { type: 'https://tools.ietf.org/html/rfc9110#section-15.5.10', title: 'Approval step 1 of PR-1044 is not open (it is Approved).', status: 409 },
      }));
    api['getApprovalInbox'].mockResolvedValue(inboxPage([inboxStep({ id: 's2', requestId: 'r2', requestNumber: 'PR-1045', version: 77 })]));

    await store.approve(store.entities()[0]);

    expect(conflict.show).toHaveBeenCalledWith('Someone else just changed PR-1044. Showing the latest version.');
    expect(store.errorOf('s1')).toBeNull();
    await vi.waitFor(() => expect(store.entities().map((s) => s.id)).toEqual(['s2']));
  });

  it('403 and 422 show the server message on the row and keep the page', async () => {
    const store = await setup();
    await store.load(1);
    api['approveStep'].mockRejectedValue(new HttpErrorResponse({ status: 403, error: { detail: 'You raised this request.' } }));
    await store.approve(store.entities()[0]);
    expect(store.errorOf('s1')).toBe('You raised this request.');
    expect(store.entities()).toHaveLength(2);

    api['rejectStep'].mockRejectedValue(new HttpErrorResponse({ status: 422, error: { detail: 'The step is not open.' } }));
    await store.reject(store.entities()[1], 'too dear');
    expect(store.errorOf('s2')).toBe('The step is not open.');
  });

  it('reject_requires_reason', async () => {
    const store = await setup();
    await store.load(1);
    const step = store.entities()[0];

    expect(await store.reject(step, '   ')).toBe(false);
    expect(api['rejectStep']).not.toHaveBeenCalled();
    expect(store.errorOf('s1')).toBe('Enter a reason for rejecting.');

    api['getApprovalInbox'].mockResolvedValue(inboxPage([inboxStep({ id: 's2', requestId: 'r2', requestNumber: 'PR-1045', version: 77 })]));
    expect(await store.reject(step, '  Over budget ')).toBe(true);
    expect(api['rejectStep']).toHaveBeenCalledWith(
      expect.objectContaining({ stepId: 's1', 'If-Match': '"1730"', body: { reason: 'Over budget' } }),
      expect.any(HttpContext),
    );
    expect(store.entities().map((s) => s.id)).toEqual(['s2']);
  });

  it('an approval push refreshes the inbox; other entities do not', async () => {
    const store = await setup();
    await store.load(1);
    api['getApprovalInbox'].mockResolvedValue(inboxPage([inboxStep({ id: 's3', requestNumber: 'PR-1046' })]));
    fake.hubs[0].emit('EntityChanged', { entity: 'budget', id: 'b', requestId: null, eventType: 'x', occurredAt: 't' });
    expect(api['getApprovalInbox']).toHaveBeenCalledTimes(1);
    fake.hubs[0].emit('EntityChanged', { entity: 'approval', id: 'r1', requestId: 'r1', eventType: 'x', occurredAt: 't' });
    await vi.waitFor(() => expect(store.entities().map((s) => s.id)).toEqual(['s3']));
  });

  it('trailing_reload: an approval push is followed by a second read, for a cancel that Approvals handles after the push', async () => {
    const store = await setup();
    await store.load(1);
    api['getApprovalInbox'].mockClear();
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    try {
      fake.hubs[0].emit('EntityChanged', { entity: 'approval', id: 'r1', requestId: 'r1', eventType: 'RequestCancelled', occurredAt: 't' });
      await vi.advanceTimersByTimeAsync(0);
      expect(api['getApprovalInbox']).toHaveBeenCalledTimes(1);

      await vi.advanceTimersByTimeAsync(environment.cache.trailingReloadMs + 10);

      expect(api['getApprovalInbox']).toHaveBeenCalledTimes(2);
    } finally {
      vi.useRealTimers();
    }
  });

  it('a failed load shows the error and keeps the rows of a background refresh', async () => {
    const store = await setup();
    await store.load(1);
    api['getApprovalInbox'].mockRejectedValue(new HttpErrorResponse({ status: 503 }));
    store.invalidate('list');
    await new Promise((r) => setTimeout(r));
    expect(store.entities()).toHaveLength(2);
    await store.load(2);
    expect(store.error()).not.toBeNull();
  });

  it('logout_clears_the_inbox', async () => {
    const store = await setup();
    await store.load(1);
    TestBed.inject(CacheRegistry).clearAll();
    expect(store.entities()).toEqual([]);
    expect(store.total()).toBe(0);
  });
});
