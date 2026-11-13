import { Component, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { AuthStore } from '../../core/auth/auth.store';
import { AppConfigService } from '../../core/config/app-config';
import { SystemAvailability } from '../../core/system/system-availability';

/** What the page says when no message was stored with the switch. */
export const DEFAULT_MAINTENANCE_TEXT =
  'The demo is paused to save hosting costs. It is switched on again on request.';

/**
 * Shown while the demo is switched off, inside the frame with only its public menu (the session check and the live connection stay off,
 * so the page makes no calls of its own). "Try again" asks the switch again and opens the welcome page once the demo is back on. The contact link comes
 * from config.json (`maintenance.contactUrl`); without one, none is shown.
 */
@Component({
  selector: 'app-maintenance-page',
  template: `
    <div class="flex justify-center px-4 py-12 sm:py-20">
      <section class="card w-full max-w-xl text-center" aria-labelledby="maintenance-title">
        <p class="text-lg font-bold tracking-tight text-slate-900" aria-hidden="true">
          <span class="text-indigo-700">&#9670;</span> Procure<span class="text-indigo-700">AI</span>
        </p>
        <span class="mx-auto mt-6 flex size-12 items-center justify-center rounded-full bg-indigo-50 text-indigo-700" aria-hidden="true">
          <svg viewBox="0 0 24 24" class="size-6" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
            <path d="M12 3v9" />
            <path d="M6.3 7.3a8 8 0 1 0 11.4 0" />
          </svg>
        </span>
        <h1 id="maintenance-title" class="mt-4 text-2xl font-bold tracking-tight text-slate-900 sm:text-3xl">This demo is switched off</h1>
        <p class="mt-4 text-slate-600" data-testid="maintenance-message">{{ text() }}</p>
        @if (!availability.message()) {
          <p class="mt-2 text-sm text-slate-500">Nothing is lost: everything is there again once it is switched back on.</p>
        }
        <div class="mt-8 flex flex-wrap justify-center gap-3">
          @if (contact(); as link) {
            <a class="btn-primary" [href]="link.url" target="_blank" rel="noopener noreferrer" data-testid="maintenance-contact">{{ link.label }}</a>
          }
          <button type="button" [class]="contact() ? 'btn-secondary' : 'btn-primary'" [disabled]="checking()" [attr.aria-busy]="checking()" (click)="tryAgain()">
            {{ checking() ? 'Checking...' : 'Try again' }}
          </button>
        </div>
        @if (stillOff()) {
          <p role="status" class="mt-4 text-sm text-slate-600">The demo is still switched off.</p>
        }
      </section>
    </div>
  `,
})
export class MaintenancePage {
  protected readonly availability = inject(SystemAvailability);
  private readonly config = inject(AppConfigService);
  private readonly auth = inject(AuthStore);
  private readonly router = inject(Router);

  protected readonly checking = signal(false);
  protected readonly stillOff = signal(false);
  protected readonly text = computed(() => this.availability.message() ?? DEFAULT_MAINTENANCE_TEXT);
  protected readonly contact = computed(() => {
    const { contactUrl, contactLabel } = this.config.config().maintenance;
    return contactUrl ? { url: contactUrl, label: contactLabel || 'Request access' } : null;
  });

  protected async tryAgain(): Promise<void> {
    this.checking.set(true);
    this.stillOff.set(false);
    try {
      const answer = await this.availability.read();
      if (!answer.enabled) {
        this.availability.apply(answer);
        this.stillOff.set(true);
        return;
      }
      // Back on. First find out who is signed in (skipped while off) and open the welcome page, and only then show the app again, so the
      // shell never renders around this page. A failed session check is no reason to stay here: the welcome page offers the sign-in.
      try {
        await this.auth.load();
      } catch {
        // Nobody counts as signed in; the welcome page leads to the sign-in.
      }
      await this.router.navigateByUrl('/');
      this.availability.apply(answer);
    } finally {
      this.checking.set(false);
    }
  }
}
