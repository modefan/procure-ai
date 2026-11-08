import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { App } from './app';
import { SystemAvailability } from './core/system/system-availability';

describe('App while the demo is switched off', () => {
  beforeEach(() => TestBed.configureTestingModule({ providers: [provideRouter([])] }));

  it('shows no shell but keeps the confirm dialog, so a question being asked (unsaved changes) is never lost', async () => {
    TestBed.inject(SystemAvailability).markDisabled(null);

    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    await fixture.whenStable();

    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('app-shell')).toBeNull();
    expect(el.querySelector('app-confirm-dialog')).not.toBeNull();
  });
});
