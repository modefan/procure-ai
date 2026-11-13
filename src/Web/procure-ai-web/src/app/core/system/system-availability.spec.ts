import { TestBed } from '@angular/core/testing';
import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { AppConfigService } from '../config/app-config';
import {
  disabledMessage,
  isSystemDisabled,
  MAINTENANCE_PATH,
  startUp,
  SYSTEM_DISABLED_TYPE,
  SystemAvailability,
} from './system-availability';

describe('SystemAvailability', () => {
  let http: HttpTestingController;
  let availability: SystemAvailability;
  const url = '/api/v1/system/availability';

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
    availability = TestBed.inject(SystemAvailability);
  });

  const answer = async (body: object | null, status = 200) => {
    const checked = availability.check();
    (await vi.waitFor(() => http.expectOne(url))).flush(body, { status, statusText: 'x' });
    return checked;
  };

  it('on: enabled and no message', async () => {
    expect(await answer({ enabled: true, message: null })).toBe(true);
    expect(availability.disabled()).toBe(false);
    expect(availability.message()).toBeNull();
  });

  it('off: disabled with the stored message', async () => {
    expect(await answer({ enabled: false, message: ' Back on Monday. ' })).toBe(false);
    expect(availability.disabled()).toBe(true);
    expect(availability.message()).toBe('Back on Monday.');
  });

  it('off without a message keeps the message empty (the page shows its default)', async () => {
    expect(await answer({ enabled: false, message: '  ' })).toBe(false);
    expect(availability.message()).toBeNull();
  });

  it('a failed read counts as on (fail open, like the gateway)', async () => {
    expect(await answer(null, 502)).toBe(true);
    expect(availability.disabled()).toBe(false);
  });

  it('a 503 system-disabled answer counts as off, with its stored message', async () => {
    expect(await answer({ type: SYSTEM_DISABLED_TYPE, detail: 'gateway default', message: null }, 503)).toBe(false);
    expect(availability.disabled()).toBe(true);
    expect(availability.message()).toBeNull();
  });

  it('read() answers without changing what the app shows; apply() shows it', async () => {
    const read = availability.read();
    (await vi.waitFor(() => http.expectOne(url))).flush({ enabled: false, message: 'Off.' });

    const result = await read;
    expect(result).toEqual({ enabled: false, message: 'Off.' });
    expect(availability.disabled()).toBe(false);

    availability.apply(result);
    expect(availability.disabled()).toBe(true);
    expect(availability.message()).toBe('Off.');
  });

  it('a read slower than maintenance.checkTimeoutMs counts as on (the start is not held up)', async () => {
    TestBed.inject(AppConfigService).config.update((c) => ({ ...c, maintenance: { ...c.maintenance, checkTimeoutMs: 30 } }));
    const started = Date.now();

    const checked = availability.check();
    const pending = await vi.waitFor(() => http.expectOne(url));

    expect(await checked).toBe(true);
    expect(Date.now() - started).toBeLessThan(2000);
    expect(availability.disabled()).toBe(false);
    pending.flush({ enabled: false, message: 'Too late.' });
    expect(availability.disabled()).toBe(false);
  });

  it('switched back on clears an earlier off', async () => {
    availability.markDisabled('Off.');
    expect(await answer({ enabled: true, message: null })).toBe(true);
    expect(availability.disabled()).toBe(false);
    expect(availability.message()).toBeNull();
  });
});

describe('isSystemDisabled', () => {
  const error = (status: number, body: unknown) => new HttpErrorResponse({ status, error: body });

  it('only a 503 with the system-disabled type', () => {
    expect(isSystemDisabled(error(503, { type: SYSTEM_DISABLED_TYPE, detail: 'x' }))).toBe(true);
    expect(isSystemDisabled(error(503, { type: 'about:blank' }))).toBe(false);
    expect(isSystemDisabled(error(503, null))).toBe(false);
    expect(isSystemDisabled(error(500, { type: SYSTEM_DISABLED_TYPE }))).toBe(false);
    expect(isSystemDisabled(new Error('x'))).toBe(false);
  });

  it('disabledMessage reads the stored message (the message extension), never the gateway detail', () => {
    expect(disabledMessage(error(503, { type: SYSTEM_DISABLED_TYPE, detail: 'x', message: ' Back soon. ' }))).toBe('Back soon.');
    expect(disabledMessage(error(503, { type: SYSTEM_DISABLED_TYPE, detail: 'gateway default', message: null }))).toBeNull();
    expect(disabledMessage(error(503, { type: SYSTEM_DISABLED_TYPE, detail: 'gateway default' }))).toBeNull();
  });
});

describe('startUp (the app initializer)', () => {
  const setUp = (enabled: boolean, path: string) => {
    const order: string[] = [];
    const availability = {
      check: vi.fn(async () => {
        order.push('check');
        return enabled;
      }),
    } as unknown as SystemAvailability;
    const deps = {
      config: { load: vi.fn(async () => void order.push('config')) },
      availability,
      auth: { load: vi.fn(async () => void order.push('auth')) },
      location: { path: () => path, replaceState: vi.fn() },
    };
    return { deps, order };
  };

  it('off: config first, then the switch; the address becomes the maintenance page and nobody is asked who is signed in', async () => {
    const { deps, order } = setUp(false, '/app/dashboard?x=1');

    await startUp(deps);

    expect(order).toEqual(['config', 'check']);
    expect(deps.location.replaceState).toHaveBeenCalledWith(MAINTENANCE_PATH);
    expect(deps.auth.load).not.toHaveBeenCalled();
  });

  it.each(['', '/', '/demos/procurement', '/demos/procurement/', '/demos/procurement/about', '/demos/procurement/about?x=1#top'])(
    'off on the public page %j: the address stays, and nobody is asked who is signed in',
    async (path) => {
      const { deps, order } = setUp(false, path);

      await startUp(deps);

      expect(order).toEqual(['config', 'check']);
      expect(deps.location.replaceState).not.toHaveBeenCalled();
      expect(deps.auth.load).not.toHaveBeenCalled();
    },
  );

  it.each(['/app/requests', '/app', '/login', '/register', '/forbidden', '/no-such-page', '/demos/other'])(
    'off on %j (needs the running system, or unknown): the maintenance page',
    async (path) => {
      const { deps } = setUp(false, path);

      await startUp(deps);

      expect(deps.location.replaceState).toHaveBeenCalledWith(MAINTENANCE_PATH);
    },
  );

  it('off on the maintenance address already: the address stays', async () => {
    const { deps } = setUp(false, MAINTENANCE_PATH);

    await startUp(deps);

    expect(deps.location.replaceState).not.toHaveBeenCalled();
  });

  it('on: the session is checked and the address is left alone', async () => {
    const { deps, order } = setUp(true, '/app/requests');

    await startUp(deps);

    expect(order).toEqual(['config', 'check', 'auth']);
    expect(deps.location.replaceState).not.toHaveBeenCalled();
  });

  it('on with a stale maintenance address: the welcome page', async () => {
    const { deps } = setUp(true, MAINTENANCE_PATH);

    await startUp(deps);

    expect(deps.location.replaceState).toHaveBeenCalledWith('/');
    expect(deps.auth.load).toHaveBeenCalled();
  });
});
