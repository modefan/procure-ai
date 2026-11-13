import { HttpErrorResponse } from '@angular/common/http';
import { inject, Injector, Service, signal } from '@angular/core';
import { AppConfigService } from '../config/app-config';

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

/**
 * The stored message of that answer (its `message` extension; null when none was stored), so the page shows the same texts whether the app
 * read the switch itself or a call was refused. Not `detail`: without a message the gateway puts its own default text there.
 */
export function disabledMessage(error: HttpErrorResponse): string | null {
  const message = (error.error as { message?: unknown }).message;
  return typeof message === 'string' && message.trim() !== '' ? message.trim() : null;
}

/** What the switch says: on or off, and the stored message while off (null: the page's default text). */
export interface AvailabilityAnswer {
  enabled: boolean;
  message: string | null;
}

/** The answer when the switch cannot be read: on (fail open, as in the gateway). */
const ON: AvailabilityAnswer = { enabled: true, message: null };

/**
 * Is the demo switched on? Access holds the switch (`GET /api/v1/system/availability`, anonymous); the gateway refuses the browser's other
 * calls while it is off. While `disabled()` the app shows only the public pages and the maintenance page: no session check, no live connection.
 */
@Service()
export class SystemAvailability {
  private readonly injector = inject(Injector);
  private readonly config = inject(AppConfigService);
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
   * Asks Access, without changing what the app shows (see {@link apply}). A failed read (gateway or Access unreachable) or one slower than
   * `maintenance.checkTimeoutMs` counts as on, as in the gateway: an unreadable switch never locks anyone out (nor holds the app's start), and
   * the app's own error handling shows what is really wrong. A late answer is ignored here; a late "off" refusal still reaches the
   * interceptor, which shows the maintenance page then.
   */
  async read(): Promise<AvailabilityAnswer> {
    let timer: ReturnType<typeof setTimeout> | undefined;
    const timedOut = new Promise<AvailabilityAnswer>((resolve) => {
      timer = setTimeout(() => resolve(ON), this.config.config().maintenance.checkTimeoutMs);
    });
    try {
      return await Promise.race([this.ask(), timedOut]);
    } finally {
      clearTimeout(timer);
    }
  }

  /** Shows what an answer says: the maintenance page while off, the app while on. */
  apply(answer: AvailabilityAnswer): void {
    this.message.set(answer.enabled ? null : answer.message);
    this.disabled.set(!answer.enabled);
  }

  /** Reads the switch and shows what it says. True when the demo is on. */
  async check(): Promise<boolean> {
    const answer = await this.read();
    this.apply(answer);
    return answer.enabled;
  }

  /** A call was refused because the demo is off (the gateway's 503): show the maintenance page from now on. */
  markDisabled(message: string | null): void {
    this.apply({ enabled: false, message });
  }

  private async ask(): Promise<AvailabilityAnswer> {
    try {
      const { SystemService } = await this.client;
      const answer = await this.injector.get(SystemService).getSystemAvailability();
      return answer.enabled ? ON : { enabled: false, message: answer.message?.trim() || null };
    } catch (error) {
      return isSystemDisabled(error) ? { enabled: false, message: disabledMessage(error) } : ON;
    }
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
 * The pages that work while the demo is switched off: they need nothing from the gateway (Welcome, the Procurement front page in its
 * "paused" state, the info page). A first load of any other address (the app, the sign-in triggers, error and unknown pages) becomes the
 * maintenance page.
 */
export const PUBLIC_WHILE_OFF: readonly string[] = ['/', '/demos/procurement', '/demos/procurement/about'];

/** True for an address that stays readable while the demo is off (see {@link PUBLIC_WHILE_OFF}); query, fragment and a trailing slash are ignored. */
export function isPublicWhileOff(path: string): boolean {
  const bare = path.split(/[?#]/)[0].replace(/\/+$/, '') || '/';
  return PUBLIC_WHILE_OFF.includes(bare);
}

/**
 * The app initializer: config.json first, then the switch. Off: an address that needs the running system becomes the maintenance page
 * before the first navigation (the public pages stay), and nothing else is asked (no session check, so no live connection either). On: a
 * stale maintenance address goes to the welcome page, and the gateway is asked who is signed in, as before.
 */
export async function startUp({ config, availability, auth, location }: StartUpDeps): Promise<void> {
  await config.load();
  const path = location.path();
  const atMaintenance = path.split(/[?#]/)[0] === MAINTENANCE_PATH;
  if (!(await availability.check())) {
    if (!atMaintenance && !isPublicWhileOff(path)) {
      location.replaceState(MAINTENANCE_PATH);
    }
    return;
  }
  if (atMaintenance) {
    location.replaceState('/');
  }
  await auth.load();
}
