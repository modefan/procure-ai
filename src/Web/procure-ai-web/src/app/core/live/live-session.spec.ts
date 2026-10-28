import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, Router } from '@angular/router';
import { AuthStore } from '../auth/auth.store';
import { LiveConnection } from './live-connection';
import { LiveSession } from './live-session';

describe('LiveSession', () => {
  const tom = { userId: 'u1', name: 'Tom', tenantId: 'acme', roles: [], permissions: [] };

  it('load_fails_then_succeeds_starts_the_connection_and_logout_stops_it', async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])] });
    const live = TestBed.inject(LiveConnection);
    const start = vi.spyOn(live, 'start').mockImplementation(() => undefined);
    const stop = vi.spyOn(live, 'stop').mockResolvedValue();
    const http = TestBed.inject(HttpTestingController);
    const auth = TestBed.inject(AuthStore);
    TestBed.inject(LiveSession);
    TestBed.tick();
    start.mockClear();
    stop.mockClear();

    const first = auth.load();
    (await vi.waitFor(() => http.expectOne('/bff/user'))).flush(null, { status: 503, statusText: 'x' });
    await first;
    TestBed.tick();
    expect(start).not.toHaveBeenCalled();

    const retry = auth.load();
    (await vi.waitFor(() => http.expectOne('/bff/user'))).flush(tom);
    await retry;
    TestBed.tick();
    expect(start).toHaveBeenCalledTimes(1);

    vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
    const out = auth.logout();
    (await vi.waitFor(() => http.expectOne('/bff/logout'))).flush(null, { status: 204, statusText: 'No Content' });
    await out;
    TestBed.tick();
    expect(stop).toHaveBeenCalled();
  });
});
