import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, Router } from '@angular/router';
import { AuthStore } from './auth.store';
import { SessionExpired, SessionExpiredDialog } from './session-expired';
import { BrowserLocation } from '../browser/browser-location';

describe('SessionExpiredDialog', () => {
  const tom = { userId: 'u1', name: 'Tom', tenantId: 'acme', roles: ['requester'], permissions: [] };
  let assign: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    // jsdom has no modal dialogs.
    HTMLDialogElement.prototype.showModal = function () {
      this.setAttribute('open', '');
    };
    HTMLDialogElement.prototype.close = function () {
      this.removeAttribute('open');
      this.dispatchEvent(new Event('close'));
    };
    assign = vi.fn();
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([]), { provide: BrowserLocation, useValue: { assign } }],
    });
  });

  async function signedInAndExpired() {
    const auth = TestBed.inject(AuthStore);
    const loading = auth.load();
    (await vi.waitFor(() => TestBed.inject(HttpTestingController).expectOne('/bff/user'))).flush(tom);
    await loading;
    const fixture = TestBed.createComponent(SessionExpiredDialog);
    TestBed.inject(SessionExpired).show('/app/requests');
    fixture.detectChanges();
    await fixture.whenStable();
    return { auth, fixture };
  }

  const button = (fixture: { nativeElement: HTMLElement }, label: string) =>
    [...fixture.nativeElement.querySelectorAll('button')].find((b) => b.textContent?.trim() === label) as HTMLButtonElement;

  it('"Not now" signs the user out in the app too, so the header no longer shows them', async () => {
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
    const { auth, fixture } = await signedInAndExpired();
    expect(auth.isLoggedIn()).toBe(true);

    button(fixture, 'Not now').click();
    await fixture.whenStable();

    expect(auth.isLoggedIn()).toBe(false);
    expect(TestBed.inject(SessionExpired).returnUrl()).toBeNull();
    expect(navigate).toHaveBeenCalledWith('/');
  });

  it('"Log in again" goes to the gateway with the page to return to', async () => {
    const { auth, fixture } = await signedInAndExpired();

    button(fixture, 'Log in again').click();
    await fixture.whenStable();

    expect(assign).toHaveBeenCalledWith('/bff/login?returnUrl=%2Fapp%2Frequests');
    expect(auth.isLoggedIn()).toBe(true);
  });
});
