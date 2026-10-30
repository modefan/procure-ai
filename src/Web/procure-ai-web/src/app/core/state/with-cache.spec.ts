import { createEnvironmentInjector, EnvironmentInjector } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { AppConfigService } from '../config/app-config';
import { Logger } from '../logging/logger';
import { patchState, signalStore, withState } from '@ngrx/signals';
import { withCache } from './with-cache';
import { CacheRegistry } from './cache-registry';

const Store = signalStore({ providedIn: 'root', protectedState: false }, withState({ value: 'initial' }), withCache({ staleMs: 1000 }));
const Forever = signalStore({ providedIn: 'root', protectedState: false }, withState({ value: 'initial' }), withCache({ staleMs: Infinity }));
const Early = signalStore({ providedIn: 'root', protectedState: false }, withCache(), withState({ value: 'initial' }));
const Default = signalStore({ providedIn: 'root', protectedState: false }, withState({ value: 'initial' }), withCache());

describe('withCache', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-01-01T10:00:00Z'));
  });
  afterEach(() => vi.useRealTimers());

  const store = () => TestBed.inject(Store);
  const counting = (value = 'loaded') => vi.fn(async () => patchState(store(), { value }));

  it('fresh_key_does_not_reload', async () => {
    const loader = counting();
    await store().cacheLoad('a', loader);
    vi.advanceTimersByTime(999);

    await store().cacheLoad('a', loader);

    expect(loader).toHaveBeenCalledTimes(1);
  });

  it('stale_key_reloads_in_background_keeping_state', async () => {
    await store().cacheLoad('a', counting('first'));
    vi.advanceTimersByTime(1001);
    let finish!: () => void;
    const slow = vi.fn(
      () =>
        new Promise<void>((r) => {
          finish = () => {
            patchState(store(), { value: 'second' });
            r();
          };
        }),
    );

    const reload = store().cacheLoad('a', slow);

    expect(slow).toHaveBeenCalledTimes(1);
    expect(store().value()).toBe('first');
    finish();
    await reload;
    expect(store().value()).toBe('second');
  });

  it('same_key_in_flight_shares_one_request', async () => {
    let finish!: () => void;
    const loader = vi.fn(() => new Promise<void>((r) => (finish = r)));

    const first = store().cacheLoad('a', loader);
    const second = store().cacheLoad('a', loader);
    finish();
    await Promise.all([first, second]);

    expect(loader).toHaveBeenCalledTimes(1);
  });

  it('a_failed_load_is_not_remembered_as_fresh', async () => {
    const failing = vi.fn(async () => {
      throw new Error('boom');
    });
    await expect(store().cacheLoad('a', failing)).rejects.toThrow('boom');
    const ok = counting();

    await store().cacheLoad('a', ok);

    expect(ok).toHaveBeenCalledTimes(1);
  });

  it('invalidate_reruns_matching_loaders', async () => {
    const a = counting();
    const b = counting();
    await store().cacheLoad('list:1', a);
    await store().cacheLoad('detail:9', b);

    store().invalidate('list:1');
    await vi.waitFor(() => expect(a).toHaveBeenCalledTimes(2));
    expect(b).toHaveBeenCalledTimes(1);

    store().invalidate((key) => key.startsWith('detail'));
    await vi.waitFor(() => expect(b).toHaveBeenCalledTimes(2));
    expect(a).toHaveBeenCalledTimes(2);
  });

  it('a_string_matches_the_key_and_its_colon_children', async () => {
    const a = counting();
    const b = counting();
    await store().cacheLoad('list:1', a);
    await store().cacheLoad('list:2', b);

    store().invalidate('list');

    await vi.waitFor(() => {
      expect(a).toHaveBeenCalledTimes(2);
      expect(b).toHaveBeenCalledTimes(2);
    });
  });

  it('a_string_does_not_match_a_longer_key_that_merely_starts_with_it', async () => {
    const one = counting();
    const ten = counting();
    await store().cacheLoad('list:1', one);
    await store().cacheLoad('list:10', ten);

    store().invalidate('list:1');

    await vi.waitFor(() => expect(one).toHaveBeenCalledTimes(2));
    await Promise.resolve();
    expect(ten).toHaveBeenCalledTimes(1);
  });

  it('invalidate_during_a_load_runs_the_loader_again_afterwards', async () => {
    let finish!: () => void;
    const loader = vi.fn(() => new Promise<void>((r) => (finish = r)));
    const first = store().cacheLoad('a', loader);

    store().invalidate('a');
    expect(loader).toHaveBeenCalledTimes(1);
    finish();
    await first;

    await vi.waitFor(() => expect(loader).toHaveBeenCalledTimes(2));
    finish();
  });

  it('revalidate_all_reruns_every_loader', async () => {
    const a = counting();
    const b = counting();
    await store().cacheLoad('a', a);
    await store().cacheLoad('b', b);

    store().revalidateAll();

    await vi.waitFor(() => {
      expect(a).toHaveBeenCalledTimes(2);
      expect(b).toHaveBeenCalledTimes(2);
    });
  });

  it('lru_drops_oldest_after_50', async () => {
    const loaders = Array.from({ length: 51 }, () => counting());
    for (const [i, loader] of loaders.entries()) {
      await store().cacheLoad(`k${i}`, loader);
    }

    // k0 was dropped (loads again); k2 is still fresh.
    await store().cacheLoad('k0', loaders[0]);
    await store().cacheLoad('k2', loaders[2]);

    expect(loaders[0]).toHaveBeenCalledTimes(2);
    expect(loaders[2]).toHaveBeenCalledTimes(1);
  });

  it('lru_keeps_a_key_that_was_used_recently', async () => {
    const loaders = Array.from({ length: 50 }, () => counting());
    for (const [i, loader] of loaders.entries()) {
      await store().cacheLoad(`k${i}`, loader);
    }
    await store().cacheLoad('k0', loaders[0]); // touch k0
    await store().cacheLoad('new', counting()); // the 51st key pushes out k1, not k0

    await store().cacheLoad('k0', loaders[0]);
    await store().cacheLoad('k1', loaders[1]);

    expect(loaders[0]).toHaveBeenCalledTimes(1);
    expect(loaders[1]).toHaveBeenCalledTimes(2);
  });

  it('clear_cache_forgets_everything', async () => {
    const loader = counting();
    await store().cacheLoad('a', loader);

    store().clearCache();
    await store().cacheLoad('a', loader);

    expect(loader).toHaveBeenCalledTimes(2);
  });

  it('clear_cache_stops_a_running_load_from_being_remembered_and_bumps_the_epoch', async () => {
    let finish!: () => void;
    const loader = vi.fn(() => new Promise<void>((r) => (finish = r)));
    const epoch = store().cacheEpoch();
    const running = store().cacheLoad('a', loader);

    store().clearCache();
    finish();
    await running;
    const again = counting();
    await store().cacheLoad('a', again);

    expect(store().cacheEpoch()).toBeGreaterThan(epoch);
    expect(again).toHaveBeenCalledTimes(1);
  });

  it('reset_returns_the_state_to_what_it_was_at_creation_and_clears_the_cache', async () => {
    const loader = counting('changed');
    await store().cacheLoad('a', loader);
    expect(store().value()).toBe('changed');

    store().reset();

    expect(store().value()).toBe('initial');
    await store().cacheLoad('a', loader);
    expect(loader).toHaveBeenCalledTimes(2);
  });

  it('infinity_means_never_stale', async () => {
    const loader = vi.fn(async () => undefined);
    await TestBed.inject(Forever).cacheLoad('a', loader);
    vi.advanceTimersByTime(365 * 24 * 3600 * 1000);

    await TestBed.inject(Forever).cacheLoad('a', loader);

    expect(loader).toHaveBeenCalledTimes(1);
  });

  it('defaults_to_30_seconds', async () => {
    const s = TestBed.inject(Default);
    const loader = vi.fn(async () => undefined);
    await s.cacheLoad('a', loader);
    vi.advanceTimersByTime(29_999);
    await s.cacheLoad('a', loader);
    expect(loader).toHaveBeenCalledTimes(1);
    vi.advanceTimersByTime(2);
    await s.cacheLoad('a', loader);
    expect(loader).toHaveBeenCalledTimes(2);
  });

  it('registers_with_the_registry_so_clear_all_resets_it', async () => {
    await store().cacheLoad('a', counting('changed'));

    TestBed.inject(CacheRegistry).clearAll();

    expect(store().value()).toBe('initial');
  });

  it('revalidate_all_skips_session_long_stores_unless_forced', async () => {
    const forever = TestBed.inject(Forever);
    const loader = vi.fn(async () => undefined);
    await forever.cacheLoad('a', loader);

    forever.revalidateAll();
    await Promise.resolve();
    expect(loader).toHaveBeenCalledTimes(1);

    forever.revalidateAll({ force: true });
    await vi.waitFor(() => expect(loader).toHaveBeenCalledTimes(2));
  });

  it('an_explicit_invalidate_still_reruns_a_session_long_store', async () => {
    const forever = TestBed.inject(Forever);
    const loader = vi.fn(async () => undefined);
    await forever.cacheLoad('a', loader);

    forever.invalidate('a');

    await vi.waitFor(() => expect(loader).toHaveBeenCalledTimes(2));
  });

  it('reset_works_when_withCache_comes_before_the_state_features', async () => {
    const early = TestBed.inject(Early);
    patchState(early, { value: 'changed' });

    early.reset();

    expect(early.value()).toBe('initial');
  });

  it('a_failing_background_rerun_is_logged_not_swallowed', async () => {
    const warn = vi.fn();
    TestBed.configureTestingModule({ providers: [{ provide: Logger, useValue: { withSource: () => ({ warn, debug: vi.fn(), error: vi.fn(), info: vi.fn(), trace: vi.fn() }) } }] });
    const failing = vi.fn(async () => undefined);
    await store().cacheLoad('a', failing);
    failing.mockRejectedValueOnce(new Error('network'));

    store().invalidate('a');

    await vi.waitFor(() => expect(warn).toHaveBeenCalledTimes(1));
    expect(warn.mock.calls[0][0]).toContain('a');
  });

  it('revalidate_error_is_set_by_a_failing_background_rerun_and_cleared_by_the_next_success', async () => {
    const loader = vi.fn(async () => undefined);
    await store().cacheLoad('a', loader);
    expect(store().revalidateError()).toBe(false);
    loader.mockRejectedValueOnce(new Error('network'));

    store().invalidate('a');
    await vi.waitFor(() => expect(store().revalidateError()).toBe(true));

    store().invalidate('a');
    await vi.waitFor(() => expect(store().revalidateError()).toBe(false));
  });

  it('revalidate_error_stays_until_every_failed_key_has_loaded_again_and_clearCache_forgets_it', async () => {
    const a = vi.fn(async () => undefined);
    const b = vi.fn(async () => undefined);
    await store().cacheLoad('a', a);
    await store().cacheLoad('b', b);
    a.mockRejectedValueOnce(new Error('x'));
    b.mockRejectedValueOnce(new Error('y'));
    store().invalidate(() => true);
    await vi.waitFor(() => expect(store().revalidateError()).toBe(true));

    store().invalidate('a');
    await vi.waitFor(() => expect(a).toHaveBeenCalledTimes(3));
    expect(store().revalidateError()).toBe(true); // b is still failing

    store().clearCache();
    expect(store().revalidateError()).toBe(false);
  });

  it('a failing foreground load does not raise the refresh banner (the screen shows its own error)', async () => {
    await expect(store().cacheLoad('a', async () => Promise.reject(new Error('x')))).rejects.toThrow();
    expect(store().revalidateError()).toBe(false);
  });

  it('a_destroyed_store_is_dropped_from_the_registry', async () => {
    const injector = createEnvironmentInjector([Store], TestBed.inject(EnvironmentInjector));
    const scoped = injector.get(Store);
    await scoped.cacheLoad('a', async () => patchState(scoped, { value: 'changed' }));

    injector.destroy();
    TestBed.inject(CacheRegistry).clearAll();

    expect(scoped.value()).toBe('changed'); // not reset: it is no longer registered
  });

  describe('trailing invalidation', () => {
    it('trailing_runs_the_loaders_once_more_after_the_configured_delay', async () => {
      const loader = counting();
      await store().cacheLoad('list', loader);

      store().invalidate('list', { trailing: true });
      await vi.advanceTimersByTimeAsync(0);
      expect(loader).toHaveBeenCalledTimes(2);

      await vi.advanceTimersByTimeAsync(1999);
      expect(loader).toHaveBeenCalledTimes(2);
      await vi.advanceTimersByTimeAsync(2);
      expect(loader).toHaveBeenCalledTimes(3);
    });

    it('pushes_in_a_row_restart_the_wait_and_cause_one_extra_run', async () => {
      const loader = counting();
      await store().cacheLoad('list', loader);

      store().invalidate('list', { trailing: true });
      await vi.advanceTimersByTimeAsync(1500);
      store().invalidate('list', { trailing: true });
      await vi.advanceTimersByTimeAsync(1500);
      expect(loader).toHaveBeenCalledTimes(3); // two immediate runs, nothing trailing yet
      await vi.advanceTimersByTimeAsync(600);

      expect(loader).toHaveBeenCalledTimes(4);
    });

    it('trailing_with_predicate_is_not_merged_with_unfiltered', async () => {
      const a = counting();
      const b = counting();
      await store().cacheLoad('a', a);
      await store().cacheLoad('b', b);

      store().invalidate((key) => key === 'a', { trailing: true });
      store().invalidate((key) => key === 'b', { trailing: true });
      await vi.advanceTimersByTimeAsync(0);
      expect([a.mock.calls.length, b.mock.calls.length]).toEqual([2, 2]);
      await vi.advanceTimersByTimeAsync(2001);

      // each predicate gets its own trailing run, and only for the keys it matched
      expect([a.mock.calls.length, b.mock.calls.length]).toEqual([3, 3]);
    });

    it('trailing_with_the_same_predicate_is_merged', async () => {
      const a = counting();
      await store().cacheLoad('a', a);
      const onlyA = (key: string) => key === 'a';

      store().invalidate(onlyA, { trailing: true });
      store().invalidate(onlyA, { trailing: true });
      await vi.advanceTimersByTimeAsync(2501);

      expect(a).toHaveBeenCalledTimes(4); // two immediate runs and one trailing run
    });

    it('a_plain_invalidate_has_no_trailing_run', async () => {
      const loader = counting();
      await store().cacheLoad('list', loader);

      store().invalidate('list');
      await vi.advanceTimersByTimeAsync(10_000);

      expect(loader).toHaveBeenCalledTimes(2);
    });

    it('logout_cancels_the_pending_trailing_run', async () => {
      const loader = counting();
      await store().cacheLoad('list', loader);
      store().invalidate('list', { trailing: true });
      await vi.advanceTimersByTimeAsync(0);

      TestBed.inject(CacheRegistry).clearAll();
      await vi.advanceTimersByTimeAsync(10_000);

      expect(loader).toHaveBeenCalledTimes(2);
    });

    it('the_delay_and_the_default_stale_time_come_from_the_config', async () => {
      TestBed.inject(AppConfigService).config.update((c) => ({ ...c, cache: { staleMs: 5000, trailingReloadMs: 300 } }));
      const store = TestBed.inject(Default);
      const loader = vi.fn(async () => patchState(store, { value: 'x' }));
      await store.cacheLoad('a', loader);
      await vi.advanceTimersByTimeAsync(4999);
      await store.cacheLoad('a', loader);
      expect(loader).toHaveBeenCalledTimes(1); // still fresh at 4,999 ms (the built-in default would be 30 000)

      store.invalidate('a', { trailing: true });
      await vi.advanceTimersByTimeAsync(0);
      await vi.advanceTimersByTimeAsync(310);
      expect(loader).toHaveBeenCalledTimes(3);
    });
  });
});
