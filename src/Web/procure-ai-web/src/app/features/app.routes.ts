import { Routes } from '@angular/router';
import { permissionGuard } from '../core/auth/permission.guard';

/**
 * Everything under /app. Loaded as one lazy chunk, and only after authGuard (canMatch on the parent route) has
 * let the user in, so a visitor never downloads it. Each feature below is a lazy chunk of its own.
 */
export const APP_ROUTES: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'dashboard' },
  {
    path: 'dashboard',
    title: 'Dashboard',
    loadComponent: () => import('./dashboard/dashboard.page').then((m) => m.DashboardPage),
  },
  {
    path: 'requests',
    title: 'Requests',
    canActivate: [permissionGuard],
    data: { permission: 'requests.read' },
    loadComponent: () => import('./requests/requests.page').then((m) => m.RequestsPage),
  },
];
