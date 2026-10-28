import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { ActivatedRouteSnapshot, convertToParamMap, provideRouter, RedirectCommand } from '@angular/router';
import { OrdersService } from '../../api/purchasing/services/orders.service';
import { orderResolver } from './order.resolver';
import { order } from './orders.testing';

describe('orderResolver', () => {
  const run = (getOrder: ReturnType<typeof vi.fn>) => {
    TestBed.configureTestingModule({ providers: [provideRouter([]), { provide: OrdersService, useValue: { getOrder } }] });
    const route = { paramMap: convertToParamMap({ id: 'o1' }) } as ActivatedRouteSnapshot;
    return TestBed.runInInjectionContext(() => orderResolver(route, {} as never));
  };

  it('resolver_maps_404_to_not_found', async () => {
    const result = await run(vi.fn().mockRejectedValue(new HttpErrorResponse({ status: 404 })));
    expect(result).toBeInstanceOf(RedirectCommand);
  });

  it('gives the order it loaded', async () => {
    expect(await run(vi.fn().mockResolvedValue(order()))).toEqual(order());
  });

  it('any other failure resolves to null so the page shows its own error', async () => {
    expect(await run(vi.fn().mockRejectedValue(new HttpErrorResponse({ status: 503 })))).toBeNull();
  });
});
