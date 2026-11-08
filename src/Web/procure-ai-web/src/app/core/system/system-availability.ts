import { HttpErrorResponse } from '@angular/common/http';
import { inject, Injector, Service, signal } from '@angular/core';

/** The `type` of the gateway's 503 while the demo is switched off. */
export const SYSTEM_DISABLED_TYPE = 'https://procure-ai/problems/system-disabled';

/** The maintenance page's route. */
export const MAINTENANCE_PATH = '/maintenance';

/** True for the gateway's "the demo is switched off" answer (503 ProblemDetails of type {@link SYSTEM_DISABLED_TYPE}). */
export function isSystemDisabled(error: unknown): error is HttpErrorResponse {
  return (
    error instanceof HttpErrorResponse &&
    error.status === 503 &&
    typeof error.error === 'object' &&
    error.error !== null &&
    (error.error as { type?: unknown }).type === SYSTEM_DISABLED_TYPE
  );
}

/** The `detail` of that answer (the stored message or the gateway's default text); null when there is none. */
export function disabledDetail(error: HttpErrorResponse): string | null {
  const detail = (error.error as { detail?: unknown }).detail;
  return typeof detail === 'string' && detail.trim() !== '' ? detail : null;
}

/**
 * Is the demo switched on? Access holds the switch (`GET /api/v1/system/availability`, anonymous); the gateway refuses the browser's other
 * calls while it is off. While `disabled()` the app shows only the maintenance page: no shell, no session check, no live connection.
 */
@Service()
export class SystemAvailability {
  private readonly injector = inject(Injector);
  /**
   * The generated client, loaded as a chunk of its own: imported statically it would pull the generated client's shared code into the
   * initial bundle. The import starts when the service is created (the app initializer, before config.json is in), so it costs no extra wait.
   */
  private readonly client = import('../../api/access/services/system.service');

  /** True while the demo is switched off. */
  readonly disabled = signal(false);

  /** What the maintenance page says instead of its default text; null for the default. */
  readonly message = signal<string | null>(null);

  /**
   * Asks Access. True when the demo is on. A failed read (gateway or Access unreachable) counts as on, as in the gateway: an unreadable
   * switch never locks anyone out, and the app's own error handling shows what is really wrong.
   */
  async check(): Promise<boolean> {
    try {
      const { SystemService } = await this.client;
      const answer = await this.injector.get(SystemService).getSystemAvailability();
      this.disabled.set(!answer.enabled);
      this.message.set(answer.enabled ? null : (answer.message?.trim() || null));
    } catch (error) {
      if (isSystemDisabled(error)) {
        this.markDisabled(disabledDetail(error));
      } else {
        this.disabled.set(false);
      }
    }
    return !this.disabled();
  }

  /** A call was refused because the demo is off (the gateway's 503): show the maintenance page from now on. */
  markDisabled(message: string | null): void {
    this.message.set(message);
    this.disabled.set(true);
  }
}

/** What {@link startUp} needs (passed in, so it runs outside an injection context after its first await). */
export interface StartUpDeps {
  config: { load(): Promise<void> };
  availability: SystemAvailability;
  auth: { load(): Promise<void> };
  location: { path(): string; replaceState(path: string): void };
}

/**
 * The app initializer: config.json first, then the switch. Off: the address becomes the maintenance page before the first navigation, and
 * nothing else is asked (no session check, so no live connection either). On: a stale maintenance address goes to the welcome page, and the
 * gateway is asked who is signed in, as before.
 */
export async function startUp({ config, availability, auth, location }: StartUpDeps): Promise<void> {
  await config.load();
  const atMaintenance = location.path().split(/[?#]/)[0] === MAINTENANCE_PATH;
  if (!(await availability.check())) {
    if (!atMaintenance) {
      location.replaceState(MAINTENANCE_PATH);
    }
    return;
  }
  if (atMaintenance) {
    location.replaceState('/');
  }
  await auth.load();
}
