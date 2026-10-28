import { TestBed } from '@angular/core/testing';
import { ApprovalsService } from '../../api/approvals/services/approvals.service';
import { CacheRegistry } from '../state/cache-registry';
import { FakeHub, fakeHubFactory } from './fake-hub.testing';
import { LIVE_HUB_FACTORY } from './hub-connection';
import { LiveConnection } from './live-connection';
import { ApprovalsCountStore } from './approvals-count.store';

describe('ApprovalsCountStore', () => {
  let hubs: FakeHub[];
  let getApprovalInbox: ReturnType<typeof vi.fn>;
  let store: InstanceType<typeof ApprovalsCountStore>;
  let live: LiveConnection;

  beforeEach(async () => {
    const fake = fakeHubFactory();
    hubs = fake.hubs;
    getApprovalInbox = vi.fn().mockResolvedValue({ items: [], page: 1, pageSize: 1, total: 3 });
    TestBed.configureTestingModule({
      providers: [
        { provide: LIVE_HUB_FACTORY, useValue: fake.factory },
        { provide: ApprovalsService, useValue: { getApprovalInbox } },
      ],
    });
    live = TestBed.inject(LiveConnection);
    store = TestBed.inject(ApprovalsCountStore);
    vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    live.start();
    await vi.waitFor(() => expect(live.state()).toBe('connected'));
  });

  afterEach(() => void live.stop());

  it('reads the inbox total with a page of one', async () => {
    await store.load();
    expect(getApprovalInbox).toHaveBeenCalledWith({ page: 1, pageSize: 1 });
    expect(store.count()).toBe(3);
  });

  it('an approval push refreshes the count; other entities do not', async () => {
    await store.load();
    getApprovalInbox.mockResolvedValue({ items: [], page: 1, pageSize: 1, total: 4 });

    hubs[0].emit('EntityChanged', { entity: 'budget', id: 'b', requestId: null, eventType: 'x', occurredAt: 't' });
    expect(getApprovalInbox).toHaveBeenCalledTimes(1);

    hubs[0].emit('EntityChanged', { entity: 'approval', id: 'r1', requestId: 'r1', eventType: 'x', occurredAt: 't' });
    await vi.waitFor(() => expect(store.count()).toBe(4));
  });

  it('a push before anyone asked for the count does not call the API', () => {
    hubs[0].emit('EntityChanged', { entity: 'approval', id: 'r1', requestId: 'r1', eventType: 'x', occurredAt: 't' });
    expect(getApprovalInbox).not.toHaveBeenCalled();
  });

  it('a_failed_load_keeps_the_last_count', async () => {
    await store.load();
    getApprovalInbox.mockRejectedValue(new Error('down'));
    store.invalidate('count');
    await new Promise((r) => setTimeout(r));
    expect(store.count()).toBe(3);
  });

  it('logout_resets_the_count', async () => {
    await store.load();
    TestBed.inject(CacheRegistry).clearAll();
    expect(store.count()).toBe(0);
  });
});
