/// <reference types="vite/client" />
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { Type } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { fakeHubFactory } from '../live/fake-hub.testing';
import { LIVE_HUB_FACTORY } from '../live/hub-connection';
import { CacheRegistry } from './cache-registry';
import * as m0 from '../../core/auth/auth.store';
import * as m1 from '../../core/live/notifications.store';
import * as m2 from '../../core/live/presence.store';
import * as m3 from '../../core/reference/reference-data.store';
import * as m4 from '../../features/approvals/approvals.store';
import * as m5 from '../../features/budgets/budgets.store';
import * as m6 from '../../features/dashboard/dashboard.store';
import * as m7 from '../../features/invoices/invoice-detail.store';
import * as m8 from '../../features/invoices/invoice-form.store';
import * as m9 from '../../features/invoices/invoices.store';
import * as m10 from '../../features/orders/order-detail.store';
import * as m11 from '../../features/orders/orders.store';
import * as m12 from '../../features/requests/request-detail.store';
import * as m13 from '../../features/requests/request-form.store';
import * as m14 from '../../features/requests/requests.store';

/**
 * Every store that can forget its data (`clearCache`) must be registered with `CacheRegistry`, or logout leaves the
 * previous user's data in memory. This list holds every `*.store.ts`; the glob only lists file names (it loads nothing:
 * a module loaded through the glob would not be compiled by Angular), so a new store that is missing here fails the first test.
 */
const STORE_MODULES: Record<string, Record<string, unknown>> = {
  '../../core/auth/auth.store.ts': m0,
  '../../core/live/notifications.store.ts': m1,
  '../../core/live/presence.store.ts': m2,
  '../../core/reference/reference-data.store.ts': m3,
  '../../features/approvals/approvals.store.ts': m4,
  '../../features/budgets/budgets.store.ts': m5,
  '../../features/dashboard/dashboard.store.ts': m6,
  '../../features/invoices/invoice-detail.store.ts': m7,
  '../../features/invoices/invoice-form.store.ts': m8,
  '../../features/invoices/invoices.store.ts': m9,
  '../../features/orders/order-detail.store.ts': m10,
  '../../features/orders/orders.store.ts': m11,
  '../../features/requests/request-detail.store.ts': m12,
  '../../features/requests/request-form.store.ts': m13,
  '../../features/requests/requests.store.ts': m14,
};

type Store = { clearCache?: unknown };

describe('store registration', () => {
  it('lists_every_store_file', () => {
    const name = (path: string) => path.split('/').pop()!;
    const onDisk = Object.keys(import.meta.glob('../../**/*.store.ts')).map(name);

    expect(onDisk.sort()).toEqual(Object.keys(STORE_MODULES).map(name).sort());
  });

  it('every_cache_store_registers', () => {
    const stores = Object.entries(STORE_MODULES).flatMap(([path, exports]) =>
      Object.entries(exports)
        .filter(([, value]) => typeof value === 'function')
        .map(([name, value]) => ({ name: `${name} (${path})`, store: value as Type<Store> })),
    );
    expect(stores.length).toBeGreaterThan(10);
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), { provide: LIVE_HUB_FACTORY, useValue: fakeHubFactory().factory }, ...stores.map((s) => s.store)],
    });
    const registry = TestBed.inject(CacheRegistry);
    const registered = new Set<Store>();
    const original = registry.register.bind(registry);
    registry.register = (store) => {
      registered.add(store);
      return original(store);
    };

    // A store registers itself from its hooks, with an object that shares the methods of the injected instance (not the same reference).
    const missing: string[] = [];
    const unconstructible: string[] = [];
    for (const { name, store } of stores) {
      let instance: Store;
      try {
        instance = TestBed.inject(store);
      } catch (error) {
        unconstructible.push(`${name}: ${String(error).slice(0, 160)}`);
        continue;
      }
      if (typeof instance.clearCache === 'function' && ![...registered].some((r) => r.clearCache === instance.clearCache)) {
        missing.push(name);
      }
    }

    expect(missing).toEqual([]);
    expect(unconstructible).toEqual([]);
  });
});
