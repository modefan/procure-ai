import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { AuthStore } from '../../core/auth/auth.store';
import { AppConfigService } from '../../core/config/app-config';
import { AvailabilityAnswer, SystemAvailability } from '../../core/system/system-availability';
import { DEFAULT_MAINTENANCE_TEXT, MaintenancePage } from './maintenance.page';

describe('MaintenancePage', () => {
  const text = (el: Element | null | undefined) => el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
  let read: ReturnType<typeof vi.fn<() => Promise<AvailabilityAnswer>>>;
  let load: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    read = vi.fn<() => Promise<AvailabilityAnswer>>();
    load = vi.fn().mockResolvedValue(undefined);
    TestBed.configureTestingModule({
      providers: [provideRouter([]), { provide: AuthStore, useValue: { load } }],
    });
  });

  const render = async (message: string | null, contact?: { contactUrl: string; contactLabel: string }) => {
    const availability = TestBed.inject(SystemAvailability);
    availability.markDisabled(message);
    vi.spyOn(availability, 'read').mockImplementation(read);
    if (contact) {
      const config = TestBed.inject(AppConfigService);
      config.config.update((c) => ({ ...c, maintenance: { ...c.maintenance, ...contact } }));
    }
    const fixture = TestBed.createComponent(MaintenancePage);
    fixture.detectChanges();
    await fixture.whenStable();
    return { fixture, el: fixture.nativeElement as HTMLElement };
  };

  it('says the demo is switched off, with the default text and no contact link when none is configured', async () => {
    const { el } = await render(null);

    expect(text(el.querySelector('h1'))).toBe('This demo is switched off');
    expect(text(el.querySelector('[data-testid="maintenance-message"]'))).toBe(DEFAULT_MAINTENANCE_TEXT);
    expect(el.querySelector('[data-testid="maintenance-contact"]')).toBeNull();
    expect(el.querySelector('button')?.className).toContain('btn-primary');
  });

  it('shows the stored message instead of the default', async () => {
    const { el } = await render('Back on Monday at 08:00.');

    expect(text(el.querySelector('[data-testid="maintenance-message"]'))).toBe('Back on Monday at 08:00.');
    expect(text(el)).not.toContain(DEFAULT_MAINTENANCE_TEXT);
  });

  it('shows the configured contact link (default label when none is given)', async () => {
    const { el } = await render(null, { contactUrl: 'https://example.org/ask', contactLabel: '' });

    const link = el.querySelector<HTMLAnchorElement>('[data-testid="maintenance-contact"]');
    expect(link?.getAttribute('href')).toBe('https://example.org/ask');
    expect(text(link)).toBe('Request access');
    expect(link?.getAttribute('rel')).toContain('noopener');
    expect(el.querySelector('button')?.className).toContain('btn-secondary');
  });

  it('try again while still off says so and stays', async () => {
    read.mockResolvedValue({ enabled: false, message: null });
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
    const { fixture, el } = await render(null);

    el.querySelector('button')!.click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(text(el.querySelector('[role="status"]'))).toBe('The demo is still switched off.');
    expect(navigate).not.toHaveBeenCalled();
    expect(load).not.toHaveBeenCalled();
  });

  it('try again once it is on checks the session and opens the welcome page, and only then shows the app again', async () => {
    read.mockResolvedValue({ enabled: true, message: null });
    const availability = TestBed.inject(SystemAvailability);
    const seen: { step: string; disabled: boolean }[] = [];
    load.mockImplementation(async () => void seen.push({ step: 'load', disabled: availability.disabled() }));
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockImplementation(async () => {
      seen.push({ step: 'navigate', disabled: availability.disabled() });
      return true;
    });
    const { fixture, el } = await render(null);

    el.querySelector('button')!.click();
    await fixture.whenStable();

    expect(navigate).toHaveBeenCalledWith('/');
    expect(seen).toEqual([
      { step: 'load', disabled: true },
      { step: 'navigate', disabled: true },
    ]);
    expect(availability.disabled()).toBe(false);
  });

  it('try again once it is on still opens the welcome page when the session check fails', async () => {
    read.mockResolvedValue({ enabled: true, message: null });
    load.mockRejectedValue(new Error('gateway down'));
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
    const { fixture, el } = await render(null);

    el.querySelector('button')!.click();
    await fixture.whenStable();

    expect(navigate).toHaveBeenCalledWith('/');
    expect(TestBed.inject(SystemAvailability).disabled()).toBe(false);
  });
});
