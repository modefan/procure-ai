import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { BusyButton } from './busy-button';

@Component({
  imports: [BusyButton],
  template: `<app-busy-button [busy]="busy()" [disabled]="disabled()" busyLabel="Approving…" variant="primary" (pressed)="presses = presses + 1">Approve</app-busy-button>`,
})
class Host {
  readonly busy = signal(false);
  readonly disabled = signal(false);
  presses = 0;
}

describe('BusyButton', () => {
  function render() {
    const fixture = TestBed.createComponent(Host);
    fixture.detectChanges();
    const button = () => fixture.nativeElement.querySelector('button') as HTMLButtonElement;
    return { fixture, button };
  }

  it('shows its content and emits when pressed', () => {
    const { fixture, button } = render();
    expect(button().textContent!.trim()).toBe('Approve');
    expect(button().className).toContain('btn-primary');

    button().click();

    expect(fixture.componentInstance.presses).toBe(1);
  });

  it('while busy it is disabled, says what is happening, and ignores clicks', () => {
    const { fixture, button } = render();
    fixture.componentInstance.busy.set(true);
    fixture.detectChanges();

    expect(button().disabled).toBe(true);
    expect(button().getAttribute('aria-busy')).toBe('true');
    expect(button().textContent).toContain('Approving…');
    button().click();
    expect(fixture.componentInstance.presses).toBe(0);
  });

  it('can be disabled without being busy', () => {
    const { fixture, button } = render();
    fixture.componentInstance.disabled.set(true);
    fixture.detectChanges();

    expect(button().disabled).toBe(true);
    expect(button().getAttribute('aria-busy')).toBeNull();
  });
});
