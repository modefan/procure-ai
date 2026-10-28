import { HttpErrorResponse } from '@angular/common/http';
import { inject } from '@angular/core';
import { RedirectCommand, ResolveFn, Router } from '@angular/router';
import { RequestDto } from '../../api/requests/models/request-dto';
import { RequestsService } from '../../api/requests/services/requests.service';

/**
 * Loads the request before the detail page opens. A missing request and another tenant's request both answer 404,
 * and both go to the same not-found page, so the screen never reveals that a request exists. Any other failure
 * resolves to null: the page opens and shows its own error with a retry button.
 */
export const requestResolver: ResolveFn<RequestDto | null | RedirectCommand> = async (route) => {
  const api = inject(RequestsService);
  const router = inject(Router);
  try {
    return await api.getRequest({ id: route.paramMap.get('id') ?? '' });
  } catch (error) {
    if (error instanceof HttpErrorResponse && error.status === 404) {
      return new RedirectCommand(router.parseUrl('/not-found'), { skipLocationChange: true });
    }
    return null;
  }
};
