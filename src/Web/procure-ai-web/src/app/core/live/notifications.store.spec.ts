import { TestBed } from '@angular/core/testing';
import { BellItem } from '../../api/notifications/models/bell-item';
import { NotificationsService } from '../../api/notifications/services/notifications.service';
import { CacheRegistry } from '../state/cache-registry';
import { FakeHub, fakeHubFactory } from './fake-hub.testing';
import { LIVE_HUB_FACTORY } from './hub-connection';
import { LiveConnection } from './live-connection';
import { NotificationsStore } from './notifications.store';

const item = (n: number, read = false): BellItem => ({
  id: `n${n}`, text: `Item ${n}`, requestId: null, link: `/requests/r${n}`, createdAt: `2026-10-28T10:0${n}:00Z`, read,
});

describe('NotificationsStore', () => {
  let hubs: FakeHub[];
  let api: { listNotifications: ReturnType<typeof vi.fn>; markNotificationRead: ReturnType<typeof vi.fn>; markAllNotificationsRead: ReturnType<typeof vi.fn> };
  let store: InstanceType<typeof NotificationsStore>;
  let live: LiveConnection;

  beforeEach(async () => {
    const fake = fakeHubFactory();
    hubs = fake.hubs;
    api = {
      listNotifications: vi.fn().mockResolvedValue({ items: [item(3), item(2, true), item(1)], unread: 2 }),
      markNotificationRead: vi.fn().mockResolvedValue(undefined),
      markAllNotificationsRead: vi.fn().mockResolvedValue(undefined),
    };
    TestBed.configureTestingModule({
      providers: [
        { provide: LIVE_HUB_FACTORY, useValue: fake.factory },
        { provide: NotificationsService, useValue: api },
      ],
    });
    live = TestBed.inject(LiveConnection);
    store = TestBed.inject(NotificationsStore);
    vi.spyOn(console, 'error').mockImplementation(() => undefined);
    vi.spyOn(console, 'warn').mockImplementation(() => undefined);
  });

  afterEach(() => void live.stop());

  const connect = async () => {
    live.start();
    await vi.waitFor(() => expect(live.state()).toBe('connected'));
  };

  it('load reads the list and counts the unread', async () => {
    await store.load();

    expect(store.items().map((i) => i.id)).toEqual(['n3', 'n2', 'n1']);
    expect(store.unread()).toBe(2);
  });

  it('NotificationAdded puts the item first and counts it', async () => {
    await store.load();
    await connect();

    hubs[0].emit('NotificationAdded', item(4));

    expect(store.items()[0].id).toBe('n4');
    expect(store.unread()).toBe(3);
  });

  it('the_same_item_pushed_twice_is_shown_once', async () => {
    await store.load();
    await connect();

    hubs[0].emit('NotificationAdded', item(4));
    hubs[0].emit('NotificationAdded', item(4));
    hubs[0].emit('NotificationAdded', item(3)); // already in the loaded list

    expect(store.items().map((i) => i.id)).toEqual(['n4', 'n3', 'n2', 'n1']);
    expect(store.unread()).toBe(3);
  });

  it('markRead is optimistic and sends an Idempotency-Key', async () => {
    await store.load();
    let finish!: () => void;
    api.markNotificationRead.mockReturnValue(new Promise<void>((r) => (finish = r)));

    const marking = store.markRead('n3');
    expect(store.unread()).toBe(1);
    finish();
    await marking;

    const params = api.markNotificationRead.mock.calls[0][0];
    expect(params.id).toBe('n3');
    expect(params['Idempotency-Key']).toMatch(/^[0-9a-f-]{36}$/);
    expect(store.unread()).toBe(1);
  });

  it('mark_read_rolls_back_on_error', async () => {
    await store.load();
    api.markNotificationRead.mockRejectedValue(new Error('500'));

    await store.markRead('n3');

    expect(store.items().find((i) => i.id === 'n3')?.read).toBe(false);
    expect(store.unread()).toBe(2);
  });

  it('marking_an_item_that_is_already_read_does_nothing', async () => {
    await store.load();
    await store.markRead('n2');
    expect(api.markNotificationRead).not.toHaveBeenCalled();
  });

  it('markAllRead is optimistic and sends an Idempotency-Key', async () => {
    await store.load();
    await store.markAllRead();
    expect(store.unread()).toBe(0);
    expect(api.markAllNotificationsRead.mock.calls[0][0]['Idempotency-Key']).toBeTruthy();
  });

  it('mark_all_read_rolls_back_on_error', async () => {
    await store.load();
    api.markAllNotificationsRead.mockRejectedValue(new Error('500'));
    await store.markAllRead();
    expect(store.unread()).toBe(2);
    expect(store.items().filter((i) => !i.read).map((i) => i.id)).toEqual(['n3', 'n1']);
  });

  it('a_reconnect_reloads_the_list', async () => {
    await store.load();
    api.listNotifications.mockResolvedValue({ items: [item(5), item(3)], unread: 2 });

    await connect(); // every connect revalidates every registered store
    await vi.waitFor(() => expect(store.items().map((i) => i.id)).toEqual(['n5', 'n3']));
  });

  it('logout_empties_the_bell', async () => {
    await store.load();
    TestBed.inject(CacheRegistry).clearAll();
    expect(store.items()).toEqual([]);
    expect(store.unread()).toBe(0);
  });

  it('an_answer_that_arrives_after_logout_is_dropped', async () => {
    let answer!: (v: unknown) => void;
    api.listNotifications.mockReturnValue(new Promise((r) => (answer = r)));
    const loading = store.load();

    TestBed.inject(CacheRegistry).clearAll();
    answer({ items: [item(9)], unread: 1 });
    await loading;

    expect(store.items()).toEqual([]);
  });

  it('a_failed_load_sets_an_error_and_keeps_the_bell_usable', async () => {
    api.listNotifications.mockRejectedValue(new Error('down'));
    await store.load();
    expect(store.error()).toBe('The notifications could not be loaded.');
    expect(store.items()).toEqual([]);
  });

  it('a_push_that_arrives_while_a_reload_is_running_is_kept', async () => {
    await store.load();
    await connect();
    let answer!: (v: unknown) => void;
    api.listNotifications.mockReturnValue(new Promise((r) => (answer = r)));
    store.invalidate('list');
    hubs[0].emit('NotificationAdded', item(7)); // pushed while the GET is running; the answer does not have it
    answer({ items: [item(3), item(2, true), item(1)], unread: 2 });

    await vi.waitFor(() => expect(store.items().map((i) => i.id)).toContain('n7'));
    await new Promise((r) => setTimeout(r, 20)); // let the answer be applied
    expect(store.items().map((i) => i.id)).toEqual(['n7', 'n3', 'n2', 'n1']);
  });
});
