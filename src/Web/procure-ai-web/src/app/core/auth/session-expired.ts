import { Component, effect, ElementRef, inject, Service, signal, viewChild } from '@angular/core';
import { Router } from '@angular/router';
import { AuthStore } from './auth.store';

/** Whether the "session expired" dialog is showing, and where to return to after signing in again. */
@Service()
export class SessionExpired {
  /** The page the user was on; null while the dialog is closed. */
  readonly returnUrl = signal<string | null>(null);

  show(returnUrl: string): void {
    this.returnUrl.set(returnUrl);
  }

  dismiss(): void {
    this.returnUrl.set(null);
  }

  /** The user chose not to log in again: close the dialog, forget the user (the header must not still show them) and leave the page. */
  decline(): void {
    if (this.returnUrl() === null) {
      return;
    }
    this.dismiss();
    this.auth.endSession();
    void this.router.navigateByUrl('/');
  }

  private readonly auth = inject(AuthStore);
  private readonly router = inject(Router);
}

/**
 * Stub of screen 22 (session expired): a native modal dialog, so the browser provides the focus trap, the Esc key
 * and the inert background. A later task can restyle it; the behaviour stays.
 */
@Component({
  selector: 'app-session-expired-dialog',
  template: `
    <dialog
      #dialog
      class="m-auto w-[calc(100%-2rem)] max-w-md rounded-xl border border-slate-200 bg-white p-6 shadow-xl backdrop:bg-slate-900/50"
      aria-labelledby="session-expired-title"
      aria-describedby="session-expired-text"
      (close)="expired.decline()"
    >
      <h2 id="session-expired-title" class="text-lg font-semibold text-slate-900">Your session has expired</h2>
      <p id="session-expired-text" class="mt-2 text-sm text-slate-600">
        For your security you were signed out. Log in again to carry on where you were.
      </p>
      <div class="mt-6 flex flex-wrap justify-end gap-3">
        <button type="button" class="btn-secondary" (click)="expired.decline()">Not now</button>
        <button type="button" class="btn-primary" (click)="loginAgain()">Log in again</button>
      </div>
    </dialog>
  `,
})
export class SessionExpiredDialog {
  protected readonly expired = inject(SessionExpired);
  private readonly auth = inject(AuthStore);
  private readonly dialog = viewChild.required<ElementRef<HTMLDialogElement>>('dialog');

  constructor() {
    effect(() => {
      const element = this.dialog().nativeElement;
      if (this.expired.returnUrl() !== null && !element.open) {
        element.showModal();
      } else if (this.expired.returnUrl() === null && element.open) {
        element.close();
      }
    });
  }

  protected loginAgain(): void {
    const returnUrl = this.expired.returnUrl() ?? '/app/dashboard';
    this.expired.dismiss();
    this.auth.login(returnUrl);
  }
}
