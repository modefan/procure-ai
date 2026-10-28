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
