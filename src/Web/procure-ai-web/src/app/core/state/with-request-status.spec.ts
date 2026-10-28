import { TestBed } from '@angular/core/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { signalStore } from '@ngrx/signals';
import { withRequestStatus } from './with-request-status';

const Store = signalStore({ providedIn: 'root' }, withRequestStatus());

describe('withRequestStatus', () => {
  const store = () => TestBed.inject(Store);

  it('starts idle', () => {
    expect(store().requestStatus()).toBe('idle');
    expect(store().isLoading()).toBe(false);
    expect(store().isLoaded()).toBe(false);
    expect(store().error()).toBeNull();
  });

  it('setLoading and setLoaded move between the states', () => {
    store().setLoading();
    expect(store().isLoading()).toBe(true);
    store().setLoaded();
    expect(store().isLoaded()).toBe(true);
    expect(store().isLoading()).toBe(false);
  });

  it('setError takes the detail of a ProblemDetails body first, then the title', () => {
    store().setError(new HttpErrorResponse({ status: 409, error: { title: 'Conflict', detail: 'You cannot approve your own request.' } }));
    expect(store().error()).toBe('You cannot approve your own request.');

    store().setError(new HttpErrorResponse({ status: 400, error: { title: 'Bad request' } }));
    expect(store().error()).toBe('Bad request');
    expect(store().isLoading()).toBe(false);
    expect(store().isLoaded()).toBe(false);
  });

  it('setError never shows a raw exception message; it uses the fallback', () => {
    store().setError(new Error('Cannot read properties of undefined'));
    expect(store().error()).toBe('Something went wrong. Please try again.');

    store().setError(new Error('x'), 'The requests could not be loaded.');
    expect(store().error()).toBe('The requests could not be loaded.');
  });

  it('setLoading clears an earlier error', () => {
    store().setError('x');
    store().setLoading();
    expect(store().error()).toBeNull();
  });
});
