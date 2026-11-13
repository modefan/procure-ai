import { computed, inject, Service, Signal } from '@angular/core';
import { ApprovalsStore } from '../../features/approvals/approvals.store';
import { AuthStore } from '../auth/auth.store';
import { AppConfigService, isPageUrl, PortfolioDemo } from '../config/app-config';
import { SessionState } from './session-state';

/** One entry of the left menu. A group heading (e.g. "Admin") has an empty url and only children. */
export interface MenuItem {
  label: string;
  url: string;
  /** Not an app route: a full page load (another host opens in a new tab). */
  external: boolean;
  /** A count shown next to the label (the approvals waiting). */
  badge?: number;
  children?: MenuItem[];
}

/** A block of the menu with its heading (null: no heading). */
export interface MenuSection {
  label: string | null;
  items: MenuItem[];
}

/** The demo whose entry also holds this app's signed-in pages. */
const PROCUREMENT = 'procurement';

interface AppPage {
  path: string;
  label: string;
  /** Empty: every signed-in user. */
  permission: string;
}

/** The signed-in pages of the procurement demo, in menu order; each shows only for a user who has its permission. */
const APP_PAGES: AppPage[] = [
  { path: 'dashboard', label: 'Dashboard', permission: '' },
  { path: 'requests', label: 'Requests', permission: 'requests.read' },
  { path: 'approvals', label: 'Approvals', permission: 'requests.approve' },
  { path: 'orders', label: 'Orders', permission: 'orders.read' },
  { path: 'invoices', label: 'Invoices', permission: 'invoices.read' },
  { path: 'budgets', label: 'Budgets', permission: 'budgets.read' },
  { path: 'assistant', label: 'Assistant', permission: 'assistant.use' },
];

/** The admin pages (the "Admin" group), in menu order. Later admin screens are added here. */
const ADMIN_PAGES: AppPage[] = [
  { path: 'admin/authorization', label: 'Authorization', permission: 'admin.authorization' },
  { path: 'admin/access-check', label: 'Access check', permission: 'admin.authorization' },
  { path: 'admin/users', label: 'Users', permission: 'admin.users' },
  { path: 'admin/policies', label: 'Policies', permission: 'admin.policies' },
  { path: 'admin/system', label: 'System status', permission: 'admin.system' },
  { path: 'admin/conversations', label: 'Conversations', permission: 'admin.system' },
  { path: 'admin/data-changes', label: 'Data changes', permission: 'admin.system' },
];

/**
 * The left menu (the sidebar and the phone drawer show the same one): Welcome; the demos from `portfolio.demos`; "Under the hood" with
 * Swagger and the Aspire dashboard last (named so: the app has a Dashboard page too). Signed in with the demo on, the procurement entry also lists the pages this user may open. It reads
 * only signals the app already has (no call of its own), so it is the same while the demo is off, minus the signed-in pages.
 */
@Service()
export class Menu {
  private readonly config = inject(AppConfigService);
  private readonly auth = inject(AuthStore);
  private readonly session = inject(SessionState).active;
  private readonly approvals = inject(ApprovalsStore);

  readonly sections: Signal<MenuSection[]> = computed(() => {
    const { portfolio, observability } = this.config.config();
    const session = this.session();
    const sections: MenuSection[] = [{ label: null, items: [{ label: 'Welcome', url: '/', external: false }] }];

    const demos = portfolio.demos.map((demo) => this.demoItem(demo, session));
    if (demos.length > 0) {
      sections.push({ label: 'Demos', items: demos });
    }

    const tools: MenuItem[] = [];
    if (portfolio.swaggerUrl) {
      tools.push({ label: 'API (Swagger)', url: portfolio.swaggerUrl, external: true });
    }
    const dashboard = observability.dashboardUrl.trim();
    if (isPageUrl(dashboard)) {
      tools.push({ label: 'Aspire dashboard', url: dashboard, external: true });
    }
    if (tools.length > 0) {
      sections.push({ label: 'Under the hood', items: tools });
    }
    return sections;
  });

  private demoItem(demo: PortfolioDemo, session: boolean): MenuItem {
    const item: MenuItem = { label: demo.name, url: demo.startUrl, external: demo.external };
    if (demo.external) {
      return item; // hosted elsewhere: its own site has its own menu
    }
    const children: MenuItem[] = [];
    if (demo.infoUrl) {
      children.push({ label: 'How it is built', url: demo.infoUrl, external: false });
    }
    if (demo.key === PROCUREMENT && session) {
      children.push(...this.allowed(APP_PAGES));
      const admin = this.allowed(ADMIN_PAGES);
      if (admin.length > 0) {
        children.push({ label: 'Admin', url: '', external: false, children: admin });
      }
    }
    return children.length > 0 ? { ...item, children } : item;
  }

  private allowed(pages: AppPage[]): MenuItem[] {
    return pages
      .filter((page) => !page.permission || this.auth.hasPermission(page.permission))
      .map((page) => {
        const item: MenuItem = { label: page.label, url: `/app/${page.path}`, external: false };
        return page.path === 'approvals' ? { ...item, badge: this.approvals.total() } : item;
      });
  }
}
