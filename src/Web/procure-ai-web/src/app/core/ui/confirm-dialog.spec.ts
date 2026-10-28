import { TestBed } from '@angular/core/testing';
import { ConfirmDialog, ConfirmDialogHost } from './confirm-dialog';

describe('ConfirmDialog', () => {
  beforeEach(() => {
    HTMLDialogElement.prototype.showModal = function () {
      this.setAttribute('open', '');
    };
    HTMLDialogElement.prototype.close = function () {
      this.removeAttribute('open');
      this.dispatchEvent(new Event('close'));
    };
  });

  async function ask() {
    const fixture = TestBed.createComponent(ConfirmDialogHost);
    fixture.detectChanges();
    await fixture.whenStable();
    const answer = TestBed.inject(ConfirmDialog).ask({ title: 'Cancel PR-1001?', body: 'The budget is released.', confirm: 'Cancel request', cancel: 'Keep it' });
    fixture.detectChanges();
    await fixture.whenStable();
    const buttons = [...fixture.nativeElement.querySelectorAll('dialog button')] as HTMLButtonElement[];
    return { fixture, answer, buttons };
  }

  it('shows title, body and both labels', async () => {
    const { fixture, buttons } = await ask();

    const text = (fixture.nativeElement as HTMLElement).textContent!;
    expect(text).toContain('Cancel PR-1001?');
    expect(text).toContain('The budget is released.');
    expect(buttons.map((b) => b.textContent!.trim())).toEqual(['Keep it', 'Cancel request']);
  });

  it('resolves true on confirm', async () => {
    const { answer, buttons } = await ask();
    buttons[1].click();
    await expect(answer).resolves.toBe(true);
  });

  it('resolves false on cancel', async () => {
    const { answer, buttons } = await ask();
    buttons[0].click();
    await expect(answer).resolves.toBe(false);
  });

  it('resolves false when closed with Esc', async () => {
    const { fixture, answer } = await ask();
    (fixture.nativeElement.querySelector('dialog') as HTMLDialogElement).close();
    await expect(answer).resolves.toBe(false);
  });
});
