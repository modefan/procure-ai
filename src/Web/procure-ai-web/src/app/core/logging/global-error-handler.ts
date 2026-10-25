import { ErrorHandler, inject, Service } from '@angular/core';
import { Logger } from './logger';

/** Unhandled errors (components, promises, window errors) go through the logger instead of only the console. */
@Service()
export class GlobalErrorHandler implements ErrorHandler {
  private readonly log = inject(Logger).withSource('ErrorHandler');

  handleError(error: unknown): void {
    this.log.error('Unhandled error', error);
  }
}
