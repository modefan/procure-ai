import { computed, effect, inject, Signal } from '@angular/core';
import { patchState, signalStore, withComputed, withHooks, withMethods, withState } from '@ngrx/signals';
import { AppConfigService } from '../config/app-config';
import { AuthStore } from '../auth/auth.store';
import { Logger } from '../logging/logger';
import { CacheRegistry } from '../state/cache-registry';
import { LiveConnection } from './live-connection';
import { Viewer } from './live.models';

interface PresenceState {
  /** Everyone online in my tenant, me included (the server sends distinct users, sorted by name). */
  everyone: Viewer[];
  /** Who is viewing each watched entity, by `entity:id`, me included. */
  viewing: Record<string, Viewer[]>;
}

const NO_VIEWERS: Viewer[] = [];
const keyOf = (entity: string, id: string) => `${entity}:${id}`;

/**
 * Who is online and who is viewing what, from the hub. The lists the server sends include me; this store hides me.
 * `watch(entity, id)` is ref-counted: two components on the same entity send one `Watch`, and `Unwatch` goes when the
 * last one lets go. Active watches are re-sent after every (re)connect, because a dropped connection loses them
 * on the server. A heartbeat (`live.heartbeatMs`, 15 s; the server drops a silent connection after 45 s) runs while connected. Logout (CacheRegistry) empties everything and forgets
 * all watches, so the next user starts clean.
 */
export const PresenceStore = signalStore(
  { providedIn: 'root' },
  withState<PresenceState>({ everyone: [], viewing: {} }),
  withComputed((store) => {
    const auth = inject(AuthStore);
    return {
      /** Everyone online except me. */
      online: computed(() => store.everyone().filter((v) => v.userId !== auth.user()?.userId)),
    };
  }),
  withMethods((store) => {
    const live = inject(LiveConnection);
    const auth = inject(AuthStore);
    const log = inject(Logger).withSource('PresenceStore');
    /** Active watches and how many holders each has. */
    const watches = new Map<string, { entity: string; id: string; holders: number }>();
    /** Changes at every clear, so an answer to a `Watch` sent before logout is dropped. */
    let generation = 0;

    const send = async (entity: string, id: string): Promise<void> => {
      const mine = generation;
      try {
        const viewers = (await live.invoke('Watch', entity, id)) as Viewer[] | undefined;
        const key = keyOf(entity, id);
        if (mine === generation && watches.has(key) && Array.isArray(viewers)) {
          patchState(store, (s) => ({ viewing: { ...s.viewing, [key]: viewers } }));
        }
      } catch (error) {
        // Not connected (it is sent again on connect) or refused (for example too many watches).
        log.debug(`Watch ${entity}:${id} was not accepted`, error);
      }
    };

    return {
      /** Others viewing one entity (never me). */
      viewersOf(entity: string, id: string): Signal<Viewer[]> {
        const key = keyOf(entity, id);
        return computed(() => {
          const me = auth.user()?.userId;
          const viewers = store.viewing()[key];
          return viewers ? viewers.filter((v) => v.userId !== me) : NO_VIEWERS;
        });
      },

      /** Starts viewing an entity. Returns the function that stops it (calling it twice does nothing). */
      watch(entity: string, id: string): () => void {
        const key = keyOf(entity, id);
        const existing = watches.get(key);
        if (existing) {
          existing.holders++;
        } else {
          watches.set(key, { entity, id, holders: 1 });
          if (live.state() === 'connected') {
            void send(entity, id);
          }
        }
        const entry = watches.get(key)!;
        let released = false;
        return () => {
          if (released) {
            return;
          }
          released = true;
          if (watches.get(key) !== entry) {
            return; // cleared by logout meanwhile
          }
          entry.holders--;
          if (entry.holders > 0) {
            return;
          }
          watches.delete(key);
          patchState(store, (s) => {
            const { [key]: _gone, ...rest } = s.viewing;
            return { viewing: rest };
          });
          if (live.state() === 'connected') {
            live.invoke('Unwatch', entity, id).catch((error: unknown) => log.debug(`Unwatch ${key} failed`, error));
          }
        };
      },

      /** Sends every active watch again (after a connect or reconnect). */
      resendWatches(): void {
        for (const { entity, id } of watches.values()) {
          void send(entity, id);
        }
      },

      sendHeartbeat(): void {
        live.invoke('Heartbeat').catch((error: unknown) => log.debug('Heartbeat failed', error));
      },

      setOnline(users: Viewer[]): void {
        patchState(store, { everyone: users });
      },

      setViewers(entity: string, id: string, viewers: Viewer[]): void {
        const key = keyOf(entity, id);
        if (watches.has(key)) {
          patchState(store, (s) => ({ viewing: { ...s.viewing, [key]: viewers } }));
        }
      },

      /** Logout: forget everyone, every watch and any answer still on its way. */
      clearCache(): void {
        generation++;
        watches.clear();
        patchState(store, { everyone: [], viewing: {} });
      },
    };
  }),
  withHooks((store) => {
    const live = inject(LiveConnection);
    const config = inject(AppConfigService);
    let release: (() => void)[] = [];
    return {
      onInit() {
        release = [
          inject(CacheRegistry).register(store),
          live.on<[Viewer[]]>('OnlineChanged', (users) => store.setOnline(users)),
          live.onViewersChanged((entity, id, viewers) => store.setViewers(entity, id, viewers)),
          live.onReconnected(() => store.resendWatches()),
        ];
        effect((onCleanup) => {
          if (live.state() === 'connected') {
            const timer = setInterval(() => store.sendHeartbeat(), config.config().live.heartbeatMs);
            onCleanup(() => clearInterval(timer));
          }
        });
      },
      onDestroy() {
        release.forEach((off) => off());
      },
    };
  }),
);
