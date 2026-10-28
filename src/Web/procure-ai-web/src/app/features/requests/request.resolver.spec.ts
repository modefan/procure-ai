import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { ActivatedRouteSnapshot, provideRouter, RedirectCommand, RouterStateSnapshot, Router } from '@angular/router';
import { RequestsService } from '../../api/requests/services/requests.service';
import { requestResolver } from './request.resolver';
import { requestDto } from './request-detail.testing';

describe('requestResolver', () => {
  const route = { paramMap: { get: (k: string) => (k === 'id' ? 'r1' : null) } } as unknown as ActivatedRouteSnapshot;
  const run = (getRequest: ReturnType<typeof vi.fn>) => {
    TestBed.configureTestingModule({ providers: [provideRouter([]), { provide: RequestsService, useValue: { getRequest } }] });
    return TestBed.runInInjectionContext(() => requestResolver(route, {} as RouterStateSnapshot));
  };

  it('returns the request when it is found', async () => {
    const result = await run(vi.fn().mockResolvedValue(requestDto()));
    expect((result as { number: string }).number).toBe('PR-1043');
  });

  it('resolver_maps_404_to_not_found: a missing request and another tenant request look the same', async () => {
    const result = await run(vi.fn().mockRejectedValue(new HttpErrorResponse({ status: 404 })));
    expect(result).toBeInstanceOf(RedirectCommand);
    const target = (result as RedirectCommand).redirectTo;
    expect(TestBed.inject(Router).serializeUrl(target)).toBe('/not-found');
  });

  it('any other failure lets the page open and show its own error with a retry', async () => {
    const result = await run(vi.fn().mockRejectedValue(new HttpErrorResponse({ status: 503 })));
    expect(result).toBeNull();
  });
});
