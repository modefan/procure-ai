import { Component, computed, inject } from '@angular/core';
import { Menu, MenuContent, MenuItem, MenuTrigger } from '@angular/aria/menu';
import { AuthStore } from '../auth/auth.store';
import { roleLabel } from '../auth/role-label';
import { DevPanelStore } from '../dev-panel/dev-panel.store';
import { BellMenu } from './bell-menu';
import { LiveIndicator } from './live-indicator';
import { OnlineUsers } from './online-users';

/**
 * The session part of the top bar: who is online, the connection, the bell and the user menu (company switch, log out, "Behind the
 * scenes"). The top bar renders it only while somebody is signed in and the demo is on, and loads it as a chunk of its own (with Angular
 * Aria's menu): a visitor never needs it, and the initial bundle has no room for it.
 */
@Component({
  selector: 'app-session-menu',
  host: { class: 'ml-auto flex shrink-0 items-center gap-1 sm:gap-2' },
  imports: [BellMenu, LiveIndicator, OnlineUsers, Menu, MenuContent, MenuItem, MenuTrigger],
  template: `
    <app-online-users />
    <app-live-indicator />
    <app-bell-menu />
    <div class="relative">
      <button ngMenuTrigger #trigger="ngMenuTrigger" [menu]="userMenu" type="button" data-testid="user-menu" class="btn-ghost max-w-44 !px-2 !py-1 sm:max-w-60 sm:!px-3">
        <span class="min-w-0 text-left leading-tight">
          <span class="block truncate">{{ auth.user()?.name ?? 'Account' }}</span>
          <span class="block truncate text-xs font-normal text-slate-500">{{ roleAndTenant() }}</span>
        </span>
        <span aria-hidden="true" class="text-xs">&#9662;</span>
      </button>
      <div
        ngMenu
        #userMenu="ngMenu"
        (itemSelected)="onMenuItem($event)"
        [class.hidden]="!trigger.expanded()"
        class="absolute right-0 z-40 mt-1 min-w-52 rounded-lg border border-slate-200 bg-white py-1 shadow-lg"
        aria-label="Account"
      >
        <ng-template ngMenuContent>
          @if (switchTenants().length > 0) {
            <div role="group" aria-labelledby="switch-company-heading" class="border-b border-slate-100 pb-1">
              <div id="switch-company-heading" data-testid="switch-tenant-heading" class="px-4 pt-2 pb-1 text-xs font-semibold whitespace-nowrap text-slate-600">Switch company</div>
              @for (tenant of switchTenants(); track tenant.id) {
                <div ngMenuItem [value]="'tenant:' + tenant.id" data-testid="switch-tenant" class="menu-item truncate focus:text-indigo-900">{{ tenant.name }}</div>
              }
            </div>
          }
          <div ngMenuItem value="logout" class="menu-item focus:text-indigo-900">Log out</div>
          <div ngMenuItem value="logout-all" class="menu-item whitespace-nowrap focus:text-indigo-900">Log out everywhere</div>
          @if (devPanel.enabled()) {
            <div ngMenuItem value="dev-panel" class="menu-item border-t border-slate-100 whitespace-nowrap focus:text-indigo-900">
              Behind the scenes <span class="ml-2 text-xs text-slate-500">Alt+Shift+D</span>
            </div>
          }
        </ng-template>
      </div>
    </div>
  `,
})
export class SessionMenu {
  protected readonly auth = inject(AuthStore);
  protected readonly devPanel = inject(DevPanelStore);

  /** The other companies this user belongs to (the "Switch company" group); none unless there is more than one. */
  protected readonly switchTenants = computed(() => {
    const tenants = this.auth.tenants();
    const current = this.auth.user()?.tenantId;
    return tenants.length > 1 ? tenants.filter((tenant) => tenant.id !== current) : [];
  });

  protected readonly roleAndTenant = computed(() => {
    const user = this.auth.user();
    const role = user?.roles[0] ? roleLabel(user.roles[0]) : '';
    const tenant = user?.tenantName?.trim() || (user?.tenantId ? capitalise(user.tenantId) : '');
    return [role, tenant].filter(Boolean).join(' · ');
  });

  protected onMenuItem(value: string | undefined): void {
    if (value === 'logout') {
      void this.auth.logout();
    } else if (value === 'logout-all') {
      void this.auth.logoutAll();
    } else if (value?.startsWith('tenant:')) {
      void this.auth.switchTenant(value.slice('tenant:'.length));
    } else if (value === 'dev-panel') {
      this.devPanel.toggle();
    }
  }
}

function capitalise(text: string): string {
  return text.charAt(0).toUpperCase() + text.slice(1);
}
