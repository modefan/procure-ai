import { TestBed } from '@angular/core/testing';
import { CacheRegistry } from './cache-registry';

describe('CacheRegistry', () => {
  it('logout_clears_every_registered_store', () => {
    const registry = TestBed.inject(CacheRegistry);
    const a = { clearCache: vi.fn(), reset: vi.fn() };
    const b = { clearCache: vi.fn() };
    registry.register(a);
    registry.register(b);

    registry.clearAll();

    expect(a.clearCache).toHaveBeenCalledTimes(1);
    expect(a.reset).toHaveBeenCalledTimes(1);
    expect(b.clearCache).toHaveBeenCalledTimes(1);
  });

  it('registering_the_same_store_twice_clears_it_once', () => {
    const registry = TestBed.inject(CacheRegistry);
    const a = { clearCache: vi.fn() };
    registry.register(a);
    registry.register(a);

    registry.clearAll();

    expect(a.clearCache).toHaveBeenCalledTimes(1);
  });

  it('one_failing_store_does_not_stop_the_others', () => {
    const registry = TestBed.inject(CacheRegistry);
    const bad = {
      clearCache: vi.fn(() => {
        throw new Error('x');
      }),
    };
    const good = { clearCache: vi.fn() };
    registry.register(bad);
    registry.register(good);

    expect(() => registry.clearAll()).not.toThrow();
    expect(good.clearCache).toHaveBeenCalled();
  });

  it('register_returns_a_function_that_removes_the_store', () => {
    const registry = TestBed.inject(CacheRegistry);
    const a = { clearCache: vi.fn() };
    const deregister = registry.register(a);

    deregister();
    registry.clearAll();

    expect(a.clearCache).not.toHaveBeenCalled();
  });
});
