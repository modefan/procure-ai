import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RequestsService } from '../../api/requests/services/requests.service';
import { RequestDto } from '../../api/requests/models/request-dto';
import { AuthStore } from '../../core/auth/auth.store';
import { RequestsPage } from './requests.page';
import { RequestsStore } from './requests.store';

const dto = (n: number, o: Partial<RequestDto> = {}): RequestDto => ({
  id: `id-${n}`, number: `PR-${1000 + n}`, title: `Request ${n}`, status: 'Draft', costCenterCode: 'CC-100', supplierId: 's',
  lines: [], submittedAt: null, approvedAt: null, rejectedAt: null, rejectionReason: null, cancelledAt: null,
  cancellationReason: null, orderId: null, orderNumber: null, spentInclVat: null, spentInclVatDisplay: null, cancellationRefusedReason: null, totalExclVat: 1, totalExclVatDisplay: 'R 1,00', vat: 0, vatDisplay: 'R 0,00',
  totalInclVat: 1, totalInclVatDisplay: 'R 9 999 999,99', version: 1, ...o,
});

describe('RequestsPage', () => {
  let listRequests: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    listRequests = vi.fn();
    TestBed.configureTestingModule({ providers: [provideRouter([]), { provide: RequestsService, useValue: { listRequests } }] });
    vi.spyOn(console, 'error').mockImplementation(() => undefined);
  });

  async function render() {
    const fixture = TestBed.createComponent(RequestsPage);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  const text = (el: Element | null) => el?.textContent?.replace(/\s+/g, ' ').trim();

  it('shows number, title, cost center, the server\'s amount text and the status per request', async () => {
    listRequests.mockResolvedValue({ items: [dto(1, { status: 'Submitted' })], page: 1, pageSize: 25, total: 1 });

    const el = await render();

    const row = el.querySelector('tbody tr')!;
    expect([...row.querySelectorAll('td')].map(text)).toEqual(['PR-1001', 'Request 1', 'CC-100', 'R 9 999 999,99', 'Submitted']);
    expect(row.querySelector('td:nth-child(2)')?.getAttribute('title')).toBe('Request 1');
  });

  it('shows the empty state', async () => {
    listRequests.mockResolvedValue({ items: [], page: 1, pageSize: 25, total: 0 });

    const el = await render();

    expect(text(el)).toContain('No purchase requests yet');
    expect(el.querySelector('table')).toBeNull();
  });

  it('shows an alert with a retry when loading fails, and recovers', async () => {
    listRequests.mockRejectedValueOnce(new Error('down'));
    const el = await render();
    expect(el.querySelector('[role="alert"]')?.textContent).toContain('could not be loaded');

    listRequests.mockResolvedValueOnce({ items: [dto(1)], page: 1, pageSize: 25, total: 1 });
    (el.querySelector('[role="alert"] button') as HTMLButtonElement).click();
    await vi.waitFor(() => expect(TestBed.inject(RequestsStore).entities()).toHaveLength(1));
  });

  it('pages with labelled buttons and announces the current page', async () => {
    listRequests.mockResolvedValueOnce({ items: [dto(1)], page: 1, pageSize: 25, total: 51 });
    const fixture = TestBed.createComponent(RequestsPage);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const [previous, next] = [...el.querySelectorAll('nav[aria-label="Pagination"] button')] as HTMLButtonElement[];

    expect(text(el.querySelector('[aria-live="polite"]'))).toBe('Page 1 of 3 (51 requests)');
    expect(previous.disabled).toBe(true);

    listRequests.mockResolvedValueOnce({ items: [dto(21)], page: 2, pageSize: 25, total: 51 });
    next.click();
    await vi.waitFor(() => expect(listRequests).toHaveBeenLastCalledWith({ page: 2, pageSize: 25 }));
    await fixture.whenStable();
    fixture.detectChanges();

    expect(text(el.querySelector('[aria-live="polite"]'))).toBe('Page 2 of 3 (51 requests)');
    expect(previous.disabled).toBe(false);
  });

  it('shows "+ New request" only with requests.create, linking to the form', async () => {
    listRequests.mockResolvedValue({ items: [dto(1)], page: 1, pageSize: 25, total: 1 });
    
    TestBed.overrideProvider(AuthStore, { useValue: { hasPermission: (p: string) => p === 'requests.create' } });

    const el = await render();

    const link = [...el.querySelectorAll('a')].find((a) => text(a) === '+ New request');
    expect(link?.getAttribute('href')).toBe('/app/requests/new');
  });

  it('hides "+ New request" without requests.create', async () => {
    listRequests.mockResolvedValue({ items: [dto(1)], page: 1, pageSize: 25, total: 1 });
    TestBed.overrideProvider(AuthStore, { useValue: { hasPermission: () => false } });

    const el = await render();

    expect([...el.querySelectorAll('a')].some((a) => text(a) === '+ New request')).toBe(false);
  });

  it('filter buttons call the server and say which is pressed; the number and title link to the request', async () => {
    listRequests.mockResolvedValue({ items: [dto(1)], page: 1, pageSize: 25, total: 1 });
    TestBed.overrideProvider(AuthStore, { useValue: { hasPermission: () => true } });
    const el = await render();
    const button = (label: string) => [...el.querySelectorAll('[role="group"] button')].find((b) => text(b) === label) as HTMLButtonElement;

    button('Mine').click();
    await vi.waitFor(() => expect(listRequests).toHaveBeenLastCalledWith({ page: 1, pageSize: 25, mine: true }));
    button('Open').click();
    await vi.waitFor(() => expect(listRequests).toHaveBeenLastCalledWith({ page: 1, pageSize: 25, mine: true, status: 'Open' }));

    TestBed.tick();
    expect(button('Mine').getAttribute('aria-pressed')).toBe('true');
    expect(button('All').getAttribute('aria-pressed')).toBe('false');
    expect(button('Open').getAttribute('aria-pressed')).toBe('true');
    expect(el.querySelector('tbody a')?.getAttribute('href')).toBe('/app/requests/id-1');
  });

  it('explains an empty filtered list and offers to show everything', async () => {
    listRequests.mockResolvedValue({ items: [], page: 1, pageSize: 25, total: 0 });
    TestBed.overrideProvider(AuthStore, { useValue: { hasPermission: () => true } });
    const el = await render();

    (el.querySelector('[role="group"] button:nth-child(2)') as HTMLButtonElement).click(); // Mine
    await vi.waitFor(() => expect(listRequests).toHaveBeenCalledTimes(2));
    const fixtureEl = el;
    await vi.waitFor(() => expect(text(fixtureEl)).toContain('No requests match these filters'));
  });
});
