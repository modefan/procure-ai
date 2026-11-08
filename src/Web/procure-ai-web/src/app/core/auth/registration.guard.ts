import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AppConfigService } from '../config/app-config';

/**
 * For /register: with self-registration off (runtime config `registration.enabled`, plan 08 Task 12c) the route sends the visitor to the
 * login instead of Keycloak's "registration not allowed" error page.
 */
export const registrationGuard: CanActivateFn = () =>
  inject(AppConfigService).config().registration.enabled ? true : inject(Router).createUrlTree(['/login']);
