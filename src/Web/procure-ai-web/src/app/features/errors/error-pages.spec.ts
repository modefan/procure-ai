import { signal, Type } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { AuthStore } from '../../core/auth/auth.store';
import { ForbiddenPage } from './forbidden.page';
import { NotFoundPage } from './not-found.page';

describe('error pages', () => {
  const signedIn = signal(true);
  const text = (el: Element) => el.textContent?.replace(/\s+/g, ' ').trim() ?? '';

  beforeEach(() => {
    signedIn.set(true);
    history.replaceState(null, '');
    TestBed.configureTestingModule({
      providers: [provideRouter([]), { provide: AuthStore, useValue: { isLoggedIn: signedIn } }],
    });
  });

  const render = async (component: Type<unknown>) => {
    const fixture = TestBed.createComponent(component);
    fixture.detectChanges();
    await fixture.whenStable();
    return fixture.nativeElement as HTMLElement;
  };

  it('forbidden_names_permission_from_guard', async () => {
    const router = TestBed.inject(Router);
    vi.spyOn(router, 'currentNavigation').mockReturnValue({ extras: { state: { permission: 'admin.authorization' } } } as never);

    const el = await render(ForbiddenPage);

    expect(text(el)).toContain('This page needs the permission admin.authorization. Ask an admin if you need it.');
    expect(el.querySelector('code')?.textContent).toBe('admin.authorization');
  });

  it('forbidden_names_permission_after_a_reload (the state survives in history.state)', async () => {
    history.replaceState({ permission: 'admin.system' }, '');

    expect(text(await render(ForbiddenPage))).toContain('This page needs the permission admin.system.');
  });

  it('forbidden_without_state_shows_the_general_text', async () => {
    const el = await render(ForbiddenPage);

    expect(text(el)).toContain('You don\'t have access');
    expect(text(el)).not.toContain('needs the permission');
    expect(text(el)).toContain('Ask an admin if you need it.');
  });

  it('forbidden_ignores_a_state_that_is_not_a_plain_permission_name', async () => {
    history.replaceState({ permission: '<img src=x onerror=alert(1)>' }, '');

    expect(text(await render(ForbiddenPage))).not.toContain('needs the permission');
  });

  it('403 and 404 offer Back to dashboard when signed in, Back to welcome when not', async () => {
    for (const page of [ForbiddenPage, NotFoundPage]) {
      signedIn.set(true);
      const inside = await render(page);
      const link = [...inside.querySelectorAll('a')].find((a) => text(a) === 'Back to dashboard');
      expect(link?.getAttribute('href')).toBe('/app/dashboard');

      TestBed.resetTestingModule();
      TestBed.configureTestingModule({ providers: [provideRouter([]), { provide: AuthStore, useValue: { isLoggedIn: () => false } }] });
      const outside = await render(page);
      expect([...outside.querySelectorAll('a')].map(text)).toEqual(['Back to welcome']);
      TestBed.resetTestingModule();
      TestBed.configureTestingModule({ providers: [provideRouter([]), { provide: AuthStore, useValue: { isLoggedIn: signedIn } }] });
    }
  });

  it('404 says the page or record does not exist or is not in your company', async () => {
    expect(text(await render(NotFoundPage))).toContain("The page or record doesn't exist, or isn't in your company.");
  });
});
