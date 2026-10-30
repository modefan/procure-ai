import { TestBed } from '@angular/core/testing';
import { AppConfigService } from '../config/app-config';
import { DevPanel } from './dev-panel';
import { DevCall, DevPanelStore } from './dev-panel.store';

const TRACE = '4bf92f3577b34da6a3ce929d0e0e4736';
const base: DevCall = { method: 'GET', url: '/api/v1/requests/PR-1043', status: 200, ms: 12, coalesced: false, replayed: false, at: 1 };

describe('DevPanel', () => {
  beforeEach(() => {
    sessionStorage.clear();
    TestBed.configureTestingModule({});
  });

  const render = async (dashboardUrl: string, calls: DevCall[]) => {
    TestBed.inject(AppConfigService).config.update((c) => ({ ...c, devPanel: { enabled: true }, observability: { dashboardUrl } }));
    const store = TestBed.inject(DevPanelStore);
    store.setOpen(true);
    calls.forEach((c) => store.record(c));
    const fixture = TestBed.createComponent(DevPanel);
    fixture.detectChanges();
    await fixture.whenStable();
    return fixture.nativeElement as HTMLElement;
  };
  const text = (el: Element | null) => el?.textContent?.replace(/\s+/g, ' ').trim();

  it('trace_link_uses_dashboard_url', async () => {
    const el = await render('https://dash.test/', [{ ...base, traceId: TRACE }]);
    const link = el.querySelector<HTMLAnchorElement>('a[data-testid="trace-link"]');
    expect(link?.href).toBe(`https://dash.test/traces/detail/${TRACE}`);
    expect(link?.target).toBe('_blank');
    expect(text(el.querySelector('[data-testid="dev-call-detail"]'))).toContain('4bf92f35…736');
  });

  it('shows the trace id without a link when no dashboard is configured', async () => {
    const el = await render('', [{ ...base, traceId: TRACE }]);
    expect(el.querySelector('a[data-testid="trace-link"]')).toBeNull();
    expect(text(el.querySelector('[data-testid="dev-call-detail"]'))).toContain('4bf92f35…736');
  });

  it('describes a call in two lines', async () => {
    const el = await render('', [
      { ...base, cache: { kind: 'hit', ageMs: 412 }, coalesced: true, etag: '"v7"', tenant: 'acme', policy: 'requests.read', instance: 'requests-api/3f9a1c20' },
    ]);
    const summary = text(el.querySelector('[data-testid="dev-call-summary"]'));
    expect(summary).toContain('GET /api/v1/requests/PR-1043');
    expect(summary).toContain('200');
    expect(summary).toContain('12 ms');
    expect(summary).toContain('cache HIT (0.4 s old)');
    expect(summary).toContain('joined an identical request');
    const detail = text(el.querySelector('[data-testid="dev-call-detail"]'));
    expect(detail).toContain('ETag "v7"');
    expect(detail).toContain('tenant acme');
    expect(detail).toContain('policy requests.read ✓');
    expect(detail).toContain('requests-api/3f9a1c20');
    expect(text(el)).toContain('Makes the hidden patterns visible during a two-minute review.');
  });

  it('filters by method and clears', async () => {
    const el = await render('', [base, { ...base, method: 'POST', url: '/api/v1/requests', at: 2 }]);
    expect(el.querySelectorAll('[data-testid="dev-call"]')).toHaveLength(2);
    const select = el.querySelector<HTMLSelectElement>('[data-testid="dev-method-filter"]')!;
    select.value = 'POST';
    select.dispatchEvent(new Event('change'));
    TestBed.tick();
    expect(el.querySelectorAll('[data-testid="dev-call"]')).toHaveLength(1);
    el.querySelector<HTMLButtonElement>('[data-testid="dev-clear"]')!.click();
    TestBed.tick();
    expect(el.querySelectorAll('[data-testid="dev-call"]')).toHaveLength(0);
  });
});
