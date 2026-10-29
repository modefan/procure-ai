/**
 * A reload that runs once more a little after the last push, for data another service writes from the same event.
 *
 * Notifications and the owning service (Requests, Approvals, Purchasing, Audit) read the same event from RabbitMQ at the same
 * time, so the push often reaches the browser before the owning service has saved the change: the immediate reload can still
 * read the old data. The trailing reload (`cache.trailingReloadMs` in config.json) reads again once the owner has caught up.
 * Pushes in a row restart the wait, so a burst causes one extra reload, not one per push.
 */
export interface TrailingReload {
  /** (Re)starts the wait; `run` is called when it ends. */
  schedule(): void;
  /** Drops the pending run (logout, page closed). */
  cancel(): void;
}

export function createTrailingReload(run: () => void, delayMs: () => number): TrailingReload {
  let timer: ReturnType<typeof setTimeout> | null = null;
  const cancel = () => {
    if (timer !== null) {
      clearTimeout(timer);
      timer = null;
    }
  };
  return {
    schedule() {
      cancel();
      timer = setTimeout(() => {
        timer = null;
        run();
      }, delayMs());
    },
    cancel,
  };
}
