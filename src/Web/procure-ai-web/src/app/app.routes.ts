import { Routes } from '@angular/router';
import { authGuard } from './core/auth/auth.guard';
import { guestGuard } from './core/auth/guest.guard';
import { registrationGuard } from './core/auth/registration.guard';
import { AuthRedirectPage } from './features/auth/auth-redirect.page';
import { ForbiddenPage } from './features/errors/forbidden.page';
import { NotFoundPage } from './features/errors/not-found.page';
import { UnavailablePage } from './features/errors/unavailable.page';
import { WelcomePage } from './features/welcome/welcome.page';

export const routes: Routes = [
  // The public bundle: welcome, the two sign-in triggers and the error pages.
  { path: '', pathMatch: 'full', title: 'Welcome', component: WelcomePage },
  { path: 'login', title: 'Log in', component: AuthRedirectPage, canActivate: [guestGuard], data: { action: 'login' } },
  { path: 'register', title: 'Register', component: AuthRedirectPage, canActivate: [registrationGuard, guestGuard], data: { action: 'register' } },
  {
    // canMatch: for a visitor this route does not match, so the app chunk is never requested.
    path: 'app',
    canMatch: [authGuard],
    loadChildren: () => import('./features/app.routes').then((m) => m.APP_ROUTES),
  },
  { path: 'forbidden', title: 'No access', component: ForbiddenPage },
  { path: 'unavailable', title: 'Unavailable', component: UnavailablePage },
  // While the demo is switched off the app shows only this page (App renders it without the shell).
  {
    path: 'maintenance',
    title: 'Switched off',
    loadComponent: () => import('./features/maintenance/maintenance.page').then((m) => m.MaintenancePage),
  },
  { path: '**', title: 'Not found', component: NotFoundPage },
];
