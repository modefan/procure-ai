import { inject } from '@angular/core';
import { patchState, signalStoreFeature, withHooks, withMethods, withProps, withState } from '@ngrx/signals';
import { AppConfigService } from '../config/app-config';
import { idempotencyKey } from '../http/idempotency';
import { isConflict, problemOf } from '../http/problem-fields';
import { EntityChanged } from '../live/live.models';
import { LiveConnection } from '../live/live-connection';
import { Logger } from '../logging/logger';
import { ConflictDialog } from '../ui/conflict-dialog';
import { CacheRegistry } from './cache-registry';
import { createTrailingReload } from './trailing-reload';
import { withRequestStatus } from './with-request-status';

/** What a load answers: the value to show, its ETag, and the names of parts of it that could not be loaded. */
export interface DetailLoaded<T> {
  value: T;
  etag: string | null;
  partial?: string[];
}

export interface DetailLoadContext<T, TSeed> {
  /** A quiet refresh: what is shown stays when it fails. */
  background: boolean;
  /** What is shown now. */
  current: T | null;
  /** What the route resolver already fetched; use it instead of asking again (it is given once). */
  seed: TSeed | null;
}

export interface DetailOptions<T, TSeed = T> {
  /** The entity kind(s) the hub pushes about (`order`, `invoice` ...). */
  entity: string | string[];
  /** Loads the value for `id`. Throws when there is nothing to show; parts that failed go in `partial`. */
  load: (id: string, context: DetailLoadContext<T, TSeed>) => Promise<DetailLoaded<T>>;
  /** Shown as `error` when the first load fails. */
  errorMessage: string;
  /** Is this push about what is shown? A relevant push reloads quietly (and once more later, see `cache.trailingReloadMs`). */
  isRelevant: (change: EntityChanged, id: string, value: T | null) => boolean;
  /** The value and ETag to show while the first load runs, from the seed. Default: the seed is the value. */
  fromSeed?: (seed: TSeed) => { value: T; etag: string | null };
  /** Store-specific state to put back when a detail is opened or cleared and when a write starts, e.g. `{ fieldErrors: {} }`. */
  resetState?: object;
}

export interface WriteOptions<T, R> {
  /** Writes of the same kind share the running call (a double click); a different kind is refused while one runs. Default `write`. */
  kind?: string;
  /** For the log, e.g. "recording the delivery". */
  what: string;
  /** Shown when the write fails and `onError` has nothing better, e.g. "The request could not be cancelled. Please try again." */
  failureMessage: string;
  /** Shown with the conflict dialog on 412 / 409; the latest version is loaded after it. */
  conflictMessage: string | ((error: unknown) => string);
  /** The same parts give the same Idempotency-Key (a retry repeats the write); without parts every call gets a new key. */
  keyParts?: (string | number | null)[];
  /** Turns the answer into the new value, shown at once. */
  apply?: (result: R, current: T | null) => { value: T; etag: string | null };
  /** Load again quietly after a successful write (for what the server changed elsewhere). */
  reloadAfter?: boolean;
  /** A failure other than a conflict: may react, and may return the message to show (`null` for none, `undefined` for the default). */
  onError?: (error: unknown) => string | null | undefined;
}

/** The ETag of an entity with a `version` (what our APIs expect in `If-Match`). */
export const versionEtag = (entity: { version: number }): string => `"${entity.version}"`;

interface DetailState<T> {
  id: string | null;
  value: T | null;
  etag: string | null;
  /** Parts of the value that could not be loaded. */
  partial: string[];
  /** The kind of write that is running, if any. */
  busyKind: string | null;
  /** A refusal of the last write that belongs to no field. */
  generalError: string | null;
}

/**
 * The skeleton of a detail store: one entity by id, loaded with a request status, refreshed quietly when the hub says it
 * changed (and after a reconnect), and written with `runWrite`. Provide the store in the page's `providers`; it registers
 * with `CacheRegistry`, so logout clears it. Options may be given as a function, which runs in the injection context.
 *
 * `open(id, seed)` shows the seed at once and loads. Loads never overlap: one that is asked for while another runs becomes
 * exactly one more load afterwards. An answer for a detail that was left, or after `clearCache()`, is dropped.
 * `runWrite(fn, options)`: the write waits for the server, gets the loaded ETag (for `If-Match`) and one Idempotency-Key,
 * sets `busyKind` while it runs, and answers a 412 / 409 with the conflict dialog and a reload. Resolves true when the server agreed.
 */
export function withDetail<T, TSeed = T>(optionsOrFactory: DetailOptions<T, TSeed> | (() => DetailOptions<T, TSeed>)) {
  const resolve = () => (typeof optionsOrFactory === 'function' ? optionsOrFactory() : optionsOrFactory);
  const empty = (): DetailState<T> => ({ id: null, value: null, etag: null, partial: [], busyKind: null, generalError: null });
  return signalStoreFeature(
    // The options are resolved once per store (the factory may inject services).
    withProps(() => ({ _options: resolve() })),
    withState<DetailState<T>>(empty),
    withRequestStatus(),
    withMethods((store) => {
      const options = store._options;
      const conflict = inject(ConflictDialog);
      const config = inject(AppConfigService);
      const log = inject(Logger).withSource('Detail');
      const entityName = Array.isArray(options.entity) ? options.entity[0] : options.entity;
      const trailing = createTrailingReload(() => void refresh(), () => config.config().cache.trailingReloadMs);

      /** Changes at open and logout, so an answer on its way for the detail before is dropped. */
      let generation = 0;
      let running: Promise<void> | null = null;
      let again = false;
      let seed: TSeed | null = null;
      let working: Promise<boolean> | null = null;
      let workingKind: string | null = null;
      const keys = new Map<string, string>();
      const patch = (state: object) => patchState(store as never, state as never);
      const resetState = () => structuredClone(options.resetState ?? {});

      const loadOnce = async (background: boolean): Promise<void> => {
        const id = store.id();
        if (!id) {
          return;
        }
        const mine = generation;
        if (!background) {
          store.setLoading();
        }
        const fromSeed = seed;
        seed = null;
        try {
          const loaded = await options.load(id, { background, current: store.value() as T | null, seed: fromSeed });
          if (mine !== generation || store.id() !== id) {
            return;
          }
          patch({ value: loaded.value, etag: loaded.etag, partial: loaded.partial ?? [] });
          store.setLoaded();
        } catch (error) {
          if (mine !== generation || store.id() !== id) {
            return;
          }
          if (background && store.value()) {
            log.warn(`Refreshing the ${entityName} failed; keeping what is shown`, error);
          } else {
            log.error(`Loading the ${entityName} failed`, error);
            store.setError(error, options.errorMessage);
          }
        }
      };

      /** Runs one load; a call while one runs asks for exactly one more load afterwards. */
      const reload = (background: boolean): Promise<void> => {
        if (running) {
          again = true;
          return running;
        }
        const call: Promise<void> = loadOnce(background)
          .catch((error: unknown) => log.error(`Loading the ${entityName} failed unexpectedly`, error))
          .finally(() => {
            if (running !== call) {
              return; // open() or logout started over meanwhile
            }
            running = null;
            if (again) {
              again = false;
              void reload(true);
            }
          });
        running = call;
        return call;
      };
      const refresh = () => reload(true);

      const keyFor = (parts: (string | number | null)[] | undefined): string => {
        if (!parts) {
          return idempotencyKey();
        }
        const id = parts.join('|');
        let key = keys.get(id);
        if (!key) {
          key = idempotencyKey();
          keys.set(id, key);
        }
        return key;
      };

      const clear = () => {
        generation++;
        trailing.cancel();
        running = null;
        again = false;
        working = null;
        workingKind = null;
        seed = null;
        patch({ ...empty(), ...resetState() });
      };

      return {
        /** Shows `id`. `initial` is what the route resolver already fetched, so it is not asked for twice. */
        async open(id: string, initial: TSeed | null = null): Promise<void> {
          clear();
          seed = initial;
          const shown = initial === null ? null : (options.fromSeed ?? ((s: TSeed) => ({ value: s as unknown as T, etag: null })))(initial);
          patch({ id, value: shown?.value ?? null, etag: shown?.etag ?? null });
          await reload(false);
        },

        /** Loads again, keeping what is shown (retry button), and forgets the last write's message. */
        reload(): Promise<void> {
          patch({ generalError: null });
          return reload(true);
        },

        /** Loads again quietly, keeping the last write's message. */
        refresh,

        async runWrite<R>(fn: (etag: string | null, key: string) => Promise<R>, write: WriteOptions<T, R>): Promise<boolean> {
          const kind = write.kind ?? 'write';
          if (store.value() === null) {
            return false;
          }
          if (working) {
            // A double click shares the running call; a different action while one runs is refused, so it cannot report the other's result.
            return kind === workingKind ? working : false;
          }
          workingKind = kind;
          const mine = generation;
          patch({ busyKind: kind, generalError: null, ...resetState() });
          const etag = store.etag();
          const key = keyFor(write.keyParts);
          const call = (async () => {
            try {
              const result = await fn(etag, key);
              if (mine !== generation) {
                return true;
              }
              if (write.apply) {
                const next = write.apply(result, store.value() as T | null);
                patch({ value: next.value, etag: next.etag });
              }
              if (write.reloadAfter) {
                void reload(true);
              }
              return true;
            } catch (error) {
              if (mine !== generation) {
                return false;
              }
              if (isConflict(error)) {
                await conflict.show(typeof write.conflictMessage === 'function' ? write.conflictMessage(error) : write.conflictMessage);
                await reload(true);
              } else {
                log.warn(`${write.what} failed`, error);
                const custom = write.onError?.(error);
                patch({ generalError: custom === undefined ? (problemOf(error).general ?? write.failureMessage) : custom });
              }
              return false;
            } finally {
              if (mine === generation) {
                working = null;
                workingKind = null;
                patch({ busyKind: null });
              }
            }
          })();
          working = call;
          return call;
        },

        /** A push or a reconnect: refresh quietly when it is about what is shown. */
        onChange(change: EntityChanged): void {
          const id = store.id();
          if (id && options.isRelevant(change, id, store.value() as T | null)) {
            void reload(true);
            trailing.schedule();
          }
        },

        /** Stops the pending trailing load (the page is going away). */
        stop(): void {
          trailing.cancel();
        },

        /** Logout: forget everything and drop answers still on their way. */
        clearCache(): void {
          clear();
        },
      };
    }),
    withHooks((store) => {
      let release: (() => void)[] = [];
      return {
        onInit() {
          const live = inject(LiveConnection);
          const entities = store._options.entity;
          release = [
            inject(CacheRegistry).register(store),
            ...(Array.isArray(entities) ? entities : [entities]).map((entity) => live.onEntityChanged(entity, (change) => store.onChange(change))),
            live.onReconnected(() => void store.refresh()),
          ];
        },
        onDestroy() {
          release.forEach((off) => off());
          store.stop();
        },
      };
    }),
  );
}
