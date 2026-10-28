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
