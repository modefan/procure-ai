import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { App } from './app';
import { SystemAvailability } from './core/system/system-availability';

describe('App while the demo is switched off', () => {
  beforeEach(() => TestBed.configureTestingModule({ providers: [provideRouter([])] }));

  it('keeps the frame with its public menu, drops the session dialogs, and keeps the confirm dialog (a question being asked is never lost)', async () => {
    TestBed.inject(SystemAvailability).markDisabled(null);

    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    await fixture.whenStable();

    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('app-frame')).not.toBeNull();
    expect(el.querySelector('nav[aria-label=Main]')?.textContent).toContain('Welcome');
    expect(el.querySelector('[data-testid=user-menu]')).toBeNull();
    expect(el.querySelector('app-session-expired-dialog')).toBeNull();
    expect(el.querySelector('app-conflict-dialog')).toBeNull();
    expect(el.querySelector('app-confirm-dialog')).not.toBeNull();
  });
});
