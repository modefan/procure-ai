import { TestBed } from '@angular/core/testing';
import { ConflictDialog, ConflictDialogHost } from './conflict-dialog';

describe('ConflictDialog', () => {
  beforeEach(() => {
    // jsdom has no modal dialogs.
    HTMLDialogElement.prototype.showModal = function () {
      this.setAttribute('open', '');
    };
    HTMLDialogElement.prototype.close = function () {
      this.removeAttribute('open');
      this.dispatchEvent(new Event('close'));
    };
  });

  async function render() {
    const fixture = TestBed.createComponent(ConflictDialogHost);
    fixture.detectChanges();
    await fixture.whenStable();
    return fixture;
  }

  it('shows the message in a dialog and resolves when the user acknowledges it', async () => {
    const fixture = await render();
    const shown = TestBed.inject(ConflictDialog).show('PR-1001 was changed by Ben.');
    let resolved = false;
    void shown.then(() => (resolved = true));
    fixture.detectChanges();
    await fixture.whenStable();

    const dialog = fixture.nativeElement.querySelector('dialog') as HTMLDialogElement;
    expect(dialog.hasAttribute('open')).toBe(true);
    expect(dialog.textContent).toContain('PR-1001 was changed by Ben.');
    expect(resolved).toBe(false);

    (dialog.querySelector('button') as HTMLButtonElement).click();
    await shown;
    fixture.detectChanges();
    await fixture.whenStable();

    expect(resolved).toBe(true);
    expect(dialog.hasAttribute('open')).toBe(false);
  });

  it('resolves too when the dialog is closed with Esc', async () => {
    const fixture = await render();
    const shown = TestBed.inject(ConflictDialog).show('x');
    fixture.detectChanges();
    await fixture.whenStable();

    (fixture.nativeElement.querySelector('dialog') as HTMLDialogElement).close();

    await expect(shown).resolves.toBeUndefined();
  });

  it('a second call while open shares the open dialog and its promise', async () => {
    const fixture = await render();
    const service = TestBed.inject(ConflictDialog);
    const first = service.show('one');
    const second = service.show('two');
    fixture.detectChanges();
    await fixture.whenStable();

    expect(fixture.nativeElement.querySelectorAll('dialog')).toHaveLength(1);
    (fixture.nativeElement.querySelector('dialog button') as HTMLButtonElement).click();
    await Promise.all([first, second]);
  });
});
