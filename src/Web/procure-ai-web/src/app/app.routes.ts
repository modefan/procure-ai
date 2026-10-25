import { Routes } from '@angular/router';
import { authGuard } from './core/auth/auth.guard';
import { guestGuard } from './core/auth/guest.guard';
import { AuthRedirectPage } from './features/auth/auth-redirect.page';
import { ForbiddenPage } from './features/errors/forbidden.page';
import { NotFoundPage } from './features/errors/not-found.page';
import { WelcomePage } from './features/welcome/welcome.page';

export const routes: Routes = [
  // The public bundle: welcome, the two sign-in triggers and the error pages.
  { path: '', pathMatch: 'full', title: 'Welcome', component: WelcomePage },
  { path: 'login', title: 'Log in', component: AuthRedirectPage, canActivate: [guestGuard], data: { action: 'login' } },
  { path: 'register', title: 'Register', component: AuthRedirectPage, canActivate: [guestGuard], data: { action: 'register' } },
  {
    // canMatch: for a visitor this route does not match, so the app chunk is never requested.
    path: 'app',
    canMatch: [authGuard],
    loadChildren: () => import('./features/app.routes').then((m) => m.APP_ROUTES),
  },
  { path: 'forbidden', title: 'No access', component: ForbiddenPage },
  { path: '**', title: 'Not found', component: NotFoundPage },
];
