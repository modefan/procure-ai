import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { MenuDrawer } from './menu-drawer';

@Component({
  imports: [MenuDrawer],
  template: `
    <button type="button" id="opener" (click)="open.set(true)">Menu</button>
    <app-menu-drawer [(open)]="open" />
  `,
})
class Host {
  readonly open = signal(false);
}

describe('MenuDrawer', () => {
  beforeEach(() => {
    HTMLDialogElement.prototype.showModal = function () {
      this.setAttribute('open', '');
    };
    HTMLDialogElement.prototype.close = function () {
      this.removeAttribute('open');
      this.dispatchEvent(new Event('close'));
    };
    TestBed.configureTestingModule({ providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()] });
  });

  const tab = (shiftKey = false) =>
    (document.activeElement as HTMLElement).dispatchEvent(new KeyboardEvent('keydown', { key: 'Tab', shiftKey, bubbles: true, cancelable: true }));

  it('opens_traps_focus_escape_returns_focus', async () => {
    const fixture = TestBed.createComponent(Host);
    document.body.appendChild(fixture.nativeElement);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const opener = el.querySelector<HTMLButtonElement>('#opener')!;
    const dialog = () => el.querySelector('dialog[data-testid=menu-drawer]') as HTMLDialogElement;

    opener.focus();
    opener.click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(dialog().hasAttribute('open')).toBe(true);
    expect(dialog().getAttribute('aria-label')).toBe('Menu');
    const focusable = [...dialog().querySelectorAll<HTMLElement>('a[href], button')];
    expect(focusable.length).toBeGreaterThan(2);
    const [first, last] = [focusable[0], focusable.at(-1)!];
    // Focus moves into the drawer when it opens.
    expect(first.contains(document.activeElement) || document.activeElement === first).toBe(true);

    // Tab on the last control comes back to the first; Shift+Tab on the first goes to the last.
    last.focus();
    expect(tab()).toBe(false); // the browser's own move is cancelled
    expect(document.activeElement).toBe(first);
    expect(tab(true)).toBe(false);
    expect(document.activeElement).toBe(last);

    // Escape closes it and gives focus back to the button that opened it.
    last.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }));
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(fixture.componentInstance.open()).toBe(false);
    expect(dialog().hasAttribute('open')).toBe(false);
    expect(document.activeElement).toBe(opener);
    fixture.nativeElement.remove();
  });

  it('a click on the backdrop (the dialog itself) closes it; a click inside does not', async () => {
    const fixture = TestBed.createComponent(Host);
    fixture.componentInstance.open.set(true);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    const dialog = (fixture.nativeElement as HTMLElement).querySelector('dialog') as HTMLDialogElement;

    dialog.querySelector('nav')!.click();
    fixture.detectChanges();
    expect(fixture.componentInstance.open()).toBe(true);

    dialog.click();
    fixture.detectChanges();
    expect(fixture.componentInstance.open()).toBe(false);
  });
});
