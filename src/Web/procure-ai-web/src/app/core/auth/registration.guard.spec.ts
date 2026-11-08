import { TestBed } from '@angular/core/testing';
import { provideRouter, Router, UrlTree } from '@angular/router';
import { registrationGuard } from './registration.guard';
import { AppConfigService } from '../config/app-config';

describe('registrationGuard', () => {
  beforeEach(() => TestBed.configureTestingModule({ providers: [provideRouter([])] }));

  const run = () => TestBed.runInInjectionContext(() => registrationGuard({} as never, {} as never));

  it('sends /register to /login while self-registration is off (the default)', () => {
    const result = run();
    expect(result instanceof UrlTree).toBe(true);
    expect(TestBed.inject(Router).serializeUrl(result as UrlTree)).toBe('/login');
  });

  it('lets /register through when the runtime config turns registration on', () => {
    const config = TestBed.inject(AppConfigService).config;
    config.set({ ...config(), registration: { enabled: true } });
    expect(run()).toBe(true);
  });
});
