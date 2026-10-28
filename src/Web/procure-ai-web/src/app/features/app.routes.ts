import { Routes } from '@angular/router';
import { permissionGuard } from '../core/auth/permission.guard';
import { unsavedChangesGuard } from '../core/auth/unsaved-changes.guard';
import { requestResolver } from './requests/request.resolver';

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
  {
    // Before ':id' routes, so "new" is not read as an id.
    path: 'requests/new',
    title: 'New request',
    canActivate: [permissionGuard],
    canDeactivate: [unsavedChangesGuard],
    data: { permission: 'requests.create' },
    loadComponent: () => import('./requests/request-form.page').then((m) => m.RequestFormPage),
  },
  {
    path: 'requests/:id/edit',
    title: 'Edit request',
    canActivate: [permissionGuard],
    canDeactivate: [unsavedChangesGuard],
    data: { permission: 'requests.create' },
    loadComponent: () => import('./requests/request-form.page').then((m) => m.RequestFormPage),
  },
  {
    path: 'requests/:id',
    title: 'Request',
    canActivate: [permissionGuard],
    data: { permission: 'requests.read' },
    resolve: { request: requestResolver },
    loadComponent: () => import('./requests/request-detail.page').then((m) => m.RequestDetailPage),
  },
];
