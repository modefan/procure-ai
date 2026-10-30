import { signal } from '@angular/core';
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

  it('revalidateAll_asks_every_store_that_can_and_survives_a_failing_one', () => {
    const registry = TestBed.inject(CacheRegistry);
    vi.spyOn(console, 'error').mockImplementation(() => undefined);
    const bad = { clearCache: vi.fn(), revalidateAll: vi.fn(() => { throw new Error('x'); }) };
    const good = { clearCache: vi.fn(), revalidateAll: vi.fn() };
    const plain = { clearCache: vi.fn() };
    registry.register(bad);
    registry.register(good);
    registry.register(plain);

    registry.revalidateAll();

    expect(bad.revalidateAll).toHaveBeenCalledTimes(1);
    expect(good.revalidateAll).toHaveBeenCalledTimes(1);
  });
  it('revalidateErrors_is_true_while_any_store_reports_one, and retryRevalidation_retries_only_those', () => {
    const registry = TestBed.inject(CacheRegistry);
    const failing = signal(true);
    const sick = { clearCache: vi.fn(), revalidateAll: vi.fn(), revalidateError: () => failing() };
    const fine = { clearCache: vi.fn(), revalidateAll: vi.fn(), revalidateError: () => false };
    const plain = { clearCache: vi.fn() };
    expect(registry.revalidateErrors()).toBe(false);
    registry.register(fine);
    registry.register(plain);
    expect(registry.revalidateErrors()).toBe(false);
    registry.register(sick);
    expect(registry.revalidateErrors()).toBe(true);

    registry.retryRevalidation();

    expect(sick.revalidateAll).toHaveBeenCalledWith({ force: true });
    expect(fine.revalidateAll).not.toHaveBeenCalled();
    failing.set(false);
    expect(registry.revalidateErrors()).toBe(false);
  });
});
