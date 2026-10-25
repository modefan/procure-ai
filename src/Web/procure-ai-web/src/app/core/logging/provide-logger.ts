import { DOCUMENT } from '@angular/common';
import { EnvironmentProviders, ErrorHandler, inject, makeEnvironmentProviders, provideAppInitializer } from '@angular/core';
import { GlobalErrorHandler } from './global-error-handler';
import { Logger } from './logger';

/**
 * Turns the logger on: its settings come from the runtime config (list `provideAppConfig()` first), unhandled
 * errors are routed to it, and what is still waiting is sent when the page is hidden or closed.
 */
export function provideLogger(): EnvironmentProviders {
  return makeEnvironmentProviders([
    { provide: ErrorHandler, useExisting: GlobalErrorHandler },
    provideAppInitializer(() => {
      const logger = inject(Logger);
      const document = inject(DOCUMENT);
      const window = document.defaultView;
      window?.addEventListener('pagehide', () => logger.flushOnPageHide());
      document.addEventListener('visibilitychange', () => {
        if (document.visibilityState === 'hidden') {
          logger.flushOnPageHide();
        }
      });
    }),
  ]);
}
