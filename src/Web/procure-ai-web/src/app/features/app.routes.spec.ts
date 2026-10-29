import { APP_ROUTES } from './app.routes';

describe('APP_ROUTES', () => {
  it('the approvals inbox is routed and needs requests.approve', async () => {
    const route = APP_ROUTES.find((r) => r.path === 'approvals');
    expect(route).toBeDefined();
    expect(route?.data?.['permission']).toBe('requests.approve');
    expect(route?.canActivate?.length).toBe(1);
    const component = await (route!.loadComponent as () => Promise<unknown>)();
    expect(component).toBeTruthy();
  });
});

describe('APP_ROUTES orders', () => {
  it('orders and orders/:id need orders.read and load their page', async () => {
    for (const path of ['orders', 'orders/:id']) {
      const route = APP_ROUTES.find((r) => r.path === path);
      expect(route, path).toBeDefined();
      expect(route?.data?.['permission']).toBe('orders.read');
      expect(route?.canActivate?.length).toBe(1);
      expect(await (route!.loadComponent as () => Promise<unknown>)()).toBeTruthy();
    }
    expect(APP_ROUTES.find((r) => r.path === 'orders/:id')?.resolve?.['order']).toBeDefined();
  });
});

describe('APP_ROUTES invoices', () => {
  it('invoices needs invoices.read, invoices/new needs invoices.create and the unsaved guard, invoices/:id resolves the invoice', async () => {
    const expected: Record<string, string> = { invoices: 'invoices.read', 'invoices/new': 'invoices.create', 'invoices/:id': 'invoices.read' };
    for (const [path, permission] of Object.entries(expected)) {
      const route = APP_ROUTES.find((r) => r.path === path);
      expect(route, path).toBeDefined();
      expect(route?.data?.['permission']).toBe(permission);
      expect(route?.canActivate?.length).toBe(1);
      expect(await (route!.loadComponent as () => Promise<unknown>)()).toBeTruthy();
    }
    expect(APP_ROUTES.find((r) => r.path === 'invoices/new')?.canDeactivate?.length).toBe(1);
    expect(APP_ROUTES.find((r) => r.path === 'invoices/:id')?.resolve?.['invoice']).toBeDefined();
    // "new" must be matched before ":id"
    const paths = APP_ROUTES.map((r) => r.path);
    expect(paths.indexOf('invoices/new')).toBeLessThan(paths.indexOf('invoices/:id'));
  });
});

describe('APP_ROUTES budgets', () => {
  it('budgets needs budgets.read and loads its page', async () => {
    const route = APP_ROUTES.find((r) => r.path === 'budgets');
    expect(route).toBeDefined();
    expect(route?.data?.['permission']).toBe('budgets.read');
    expect(route?.canActivate?.length).toBe(1);
    expect(await (route!.loadComponent as () => Promise<unknown>)()).toBeTruthy();
  });
});
