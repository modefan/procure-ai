import { HttpErrorResponse } from '@angular/common/http';
import { inject } from '@angular/core';
import { RedirectCommand, ResolveFn, Router } from '@angular/router';
import { PurchaseOrderDto } from '../../api/purchasing/models/purchase-order-dto';
import { OrdersService } from '../../api/purchasing/services/orders.service';

/**
 * Loads the order before the detail page opens. A missing order, another tenant's order and an order the caller's data
 * scope hides all answer 404, and all go to the same not-found page. Any other failure resolves to null: the page opens
 * and shows its own error with a retry button.
 */
export const orderResolver: ResolveFn<PurchaseOrderDto | null | RedirectCommand> = async (route) => {
  const api = inject(OrdersService);
  const router = inject(Router);
  try {
    return await api.getOrder({ id: route.paramMap.get('id') ?? '' });
  } catch (error) {
    if (error instanceof HttpErrorResponse && error.status === 404) {
      return new RedirectCommand(router.parseUrl('/not-found'), { skipLocationChange: true });
    }
    return null;
  }
};
