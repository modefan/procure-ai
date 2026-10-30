import { TestBed } from '@angular/core/testing';
import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { firstValueFrom } from 'rxjs';
import { AppConfigService } from '../config/app-config';
import { devPanelInterceptor } from './dev-panel.interceptor';
import { DEV_PANEL_STORAGE_KEY, devCallFrom, DevCall, DevPanelStore, MAX_DEV_CALLS, safeUrl } from './dev-panel.store';

const call = (n: number): DevCall => ({ method: 'GET', url: `/api/v1/x/${n}`, status: 200, ms: n, coalesced: false, replayed: false, at: n });

describe('dev panel store and interceptor', () => {
  let http: HttpClient;
  let backend: HttpTestingController;

  const setEnabled = (enabled: boolean) => TestBed.inject(AppConfigService).config.update((c) => ({ ...c, devPanel: { enabled } }));

  beforeEach(() => {
    sessionStorage.clear();
    TestBed.configureTestingModule({
      providers: [provideHttpClient(withInterceptors([devPanelInterceptor])), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpClient);
    backend = TestBed.inject(HttpTestingController);
  });

  it('interceptor_adds_header_only_when_open', async () => {
    setEnabled(true);
    const store = TestBed.inject(DevPanelStore);

    const closed = firstValueFrom(http.get('/api/v1/requests'));
    const first = backend.expectOne('/api/v1/requests');
    expect(first.request.headers.has('X-Dev-Panel')).toBe(false);
    first.flush([]);
    await closed;
    expect(store.calls()).toEqual([]);

    store.setOpen(true);
    const open = firstValueFrom(http.get('/api/v1/requests'));
    const second = backend.expectOne('/api/v1/requests');
    expect(second.request.headers.get('X-Dev-Panel')).toBe('1');
    second.flush([]);
    await open;
    expect(store.calls()).toHaveLength(1);
  });

  it('does not send the header to a third party', async () => {
    setEnabled(true);
    TestBed.inject(DevPanelStore).setOpen(true);
    const result = firstValueFrom(http.get('https://elsewhere.test/x'));
    const req = backend.expectOne('https://elsewhere.test/x');
    expect(req.request.headers.has('X-Dev-Panel')).toBe(false);
    req.flush({});
    await result;
    expect(TestBed.inject(DevPanelStore).calls()).toEqual([]);
  });

  it('records_cache_hit_with_age', async () => {
    setEnabled(true);
    const store = TestBed.inject(DevPanelStore);
    store.setOpen(true);

    const result = firstValueFrom(http.get('/api/v1/requests/PR-1043'));
    backend.expectOne('/api/v1/requests/PR-1043').flush(
      {},
      {
        headers: {
          'X-Cache': 'hit; age-ms=412',
          'X-Trace-Id': '4bf92f3577b34da6a3ce929d0e0e4736',
          ETag: '"v7"',
          'X-Tenant': 'acme',
          'X-Policy': 'requests.read',
          'X-Instance': 'requests-api/3f9a1c20',
        },
      },
    );
    await result;

    const [recorded] = store.calls();
    expect(recorded).toMatchObject({
      method: 'GET',
      url: '/api/v1/requests/PR-1043',
      status: 200,
      cache: { kind: 'hit', ageMs: 412 },
      coalesced: false,
      replayed: false,
      traceId: '4bf92f3577b34da6a3ce929d0e0e4736',
      etag: '"v7"',
      tenant: 'acme',
      policy: 'requests.read',
      instance: 'requests-api/3f9a1c20',
    });
  });

  it('records_joined_and_replayed', async () => {
    setEnabled(true);
    const store = TestBed.inject(DevPanelStore);
    store.setOpen(true);

    const get = firstValueFrom(http.get('/api/v1/requests'));
    backend.expectOne('/api/v1/requests').flush([], { headers: { 'X-Cache': 'miss', 'X-Coalesced': 'joined' } });
    await get;
    const post = firstValueFrom(http.post('/api/v1/requests', {}));
    backend.expectOne('/api/v1/requests').flush({}, { status: 201, statusText: 'Created', headers: { 'X-Idempotent': 'replayed' } });
    await post;

    const [replayed, joined] = store.calls();
    expect(joined).toMatchObject({ coalesced: true, replayed: false, cache: { kind: 'miss' } });
    expect(joined.cache?.ageMs).toBeUndefined();
    expect(replayed).toMatchObject({ method: 'POST', status: 201, replayed: true, coalesced: false });
  });

  it('records a failed call with its status', async () => {
    setEnabled(true);
    const store = TestBed.inject(DevPanelStore);
    store.setOpen(true);
    const result = firstValueFrom(http.get('/api/v1/requests/x')).catch(() => undefined);
    backend.expectOne('/api/v1/requests/x').flush(null, { status: 404, statusText: 'Not Found', headers: { 'X-Trace-Id': 'abc' } });
    await result;
    expect(store.calls()[0]).toMatchObject({ status: 404, traceId: 'abc' });
  });

  it('keeps_only_last_50', () => {
    const store = TestBed.inject(DevPanelStore);
    for (let i = 1; i <= MAX_DEV_CALLS + 5; i++) {
      store.record(call(i));
    }
    expect(store.calls()).toHaveLength(MAX_DEV_CALLS);
    expect(store.calls()[0].ms).toBe(MAX_DEV_CALLS + 5);
    expect(store.calls().at(-1)?.ms).toBe(6);
    store.clear();
    expect(store.calls()).toEqual([]);
  });

  it('toggle_persists_in_session_storage', () => {
    const store = TestBed.inject(DevPanelStore);
    expect(store.open()).toBe(false);
    store.toggle();
    expect(store.open()).toBe(true);
    expect(sessionStorage.getItem(DEV_PANEL_STORAGE_KEY)).toBe('1');

    TestBed.resetTestingModule();
    TestBed.configureTestingModule({});
    expect(TestBed.inject(DevPanelStore).open()).toBe(true);

    TestBed.inject(DevPanelStore).toggle();
    expect(sessionStorage.getItem(DEV_PANEL_STORAGE_KEY)).toBe('0');
  });

  it('disabled_config_adds_no_header_and_records_nothing', async () => {
    setEnabled(false);
    const store = TestBed.inject(DevPanelStore);
    store.setOpen(true);
    const result = firstValueFrom(http.get('/api/v1/requests'));
    const req = backend.expectOne('/api/v1/requests');
    expect(req.request.headers.has('X-Dev-Panel')).toBe(false);
    req.flush([], { headers: { 'X-Cache': 'hit; age-ms=1' } });
    await result;
    expect(store.calls()).toEqual([]);
  });

  it('masks secret-looking query values and drops the origin', () => {
    expect(safeUrl('https://gw.test/api/v1/x?page=2&access_token=abc&apiKey=k')).toBe('/api/v1/x?page=2&access_token=***&apiKey=***');
    expect(safeUrl('/api/v1/requests?status=Draft')).toBe('/api/v1/requests?status=Draft');
    expect(devCallFrom('get', '/a', 200, 12.4, null).method).toBe('GET');
  });
});
