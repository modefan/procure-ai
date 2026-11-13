import { afterNextRender, Component, effect, ElementRef, inject, Injector, model, viewChild } from '@angular/core';
import { Brand, MenuNav } from './menu-nav';

const FOCUSABLE = 'a[href], button:not([disabled]), [tabindex]:not([tabindex="-1"])';

/**
 * The menu below 1024 px: a drawer from the left. A native modal `<dialog>` (the rest of the page is inert while it is open), plus what
 * Angular Aria has no primitive for (22.2 has no dialog): Tab and Shift+Tab wrap inside it, Escape and a click on the backdrop close it,
 * and focus goes back to whatever opened it. Its links are only built while it is open.
 */
@Component({
  selector: 'app-menu-drawer',
  imports: [Brand, MenuNav],
  template: `
    <dialog
      #dialog
      id="menu-drawer"
      data-testid="menu-drawer"
      aria-label="Menu"
      class="m-0 mr-auto h-dvh max-h-dvh w-72 max-w-[85vw] bg-white p-0 shadow-xl backdrop:bg-slate-900/50 lg:hidden"
      (close)="open.set(false)"
      (click)="onClick($event)"
      (keydown)="onKeydown($event)"
    >
      @if (open()) {
        <div class="flex h-14 items-center justify-between gap-2 border-b border-slate-200 px-3">
          <app-brand />
          <button type="button" data-testid="menu-drawer-close" class="btn-ghost !px-2 !py-1" aria-label="Close menu" (click)="open.set(false)">
            <svg viewBox="0 0 24 24" class="size-6" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" aria-hidden="true"><path d="M6 6l12 12M18 6L6 18" /></svg>
          </button>
        </div>
        <div class="p-3">
          <app-menu-nav label="Menu" idPrefix="drawer" />
        </div>
      }
    </dialog>
  `,
})
export class MenuDrawer {
  readonly open = model(false);
  private readonly dialog = viewChild.required<ElementRef<HTMLDialogElement>>('dialog');
  /** What had focus when the drawer opened; it gets it back on close. */
  private returnFocus: HTMLElement | null = null;
  private readonly injector = inject(Injector);

  constructor() {
    effect(() => {
      const element = this.dialog().nativeElement;
      if (this.open() && !element.open) {
        this.returnFocus = document.activeElement as HTMLElement | null;
        element.showModal();
        // Once the links are rendered: the first control (the brand link) takes focus.
        afterNextRender(() => this.focusables()[0]?.focus(), { injector: this.injector });
      } else if (!this.open()) {
        if (element.open) {
          element.close();
        }
        this.returnFocus?.focus();
        this.returnFocus = null;
      }
    });
  }

  /** A click on the dark area around the drawer (the dialog element itself, not its content) closes it. */
  protected onClick(event: Event): void {
    if (event.target === this.dialog().nativeElement) {
      this.open.set(false);
    }
  }

  protected onKeydown(event: KeyboardEvent): void {
    if (event.key === 'Escape') {
      event.preventDefault(); // ours, not the browser's cancel: focus goes back the same way as for the close button
      this.open.set(false);
      return;
    }
    if (event.key !== 'Tab') {
      return;
    }
    const items = this.focusables();
    if (items.length === 0) {
      return;
    }
    const [first, last] = [items[0], items[items.length - 1]];
    if (event.shiftKey && document.activeElement === first) {
      event.preventDefault();
      last.focus();
    } else if (!event.shiftKey && document.activeElement === last) {
      event.preventDefault();
      first.focus();
    }
  }

  private focusables(): HTMLElement[] {
    return [...this.dialog().nativeElement.querySelectorAll<HTMLElement>(FOCUSABLE)];
  }
}
