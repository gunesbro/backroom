/** The longest delay a browser timer takes (2^31 - 1 ms, about 24.8 days); a longer one would fire at once. */
export const MAX_TIMER_MS = 2_147_483_647;

type Source = { times: number[]; listeners: Set<() => void> };

export type ExpiryClock = {
  /**
   * Calls `listener` each time one of `times` (ISO 8601; empty and past ones are ignored) passes, until the returned
   * function is called. Watching a `key` again replaces its times.
   */
  watch: (key: string, times: Iterable<string | null | undefined>, listener: () => void) => () => void;
};

/**
 * The ends of timed statuses (do not disturb, a custom status): the person's own today, and those of the people on
 * screen once a view shows them (none does yet; see `index.ts`).
 * The server sends nothing when such a time passes (it only leaves expired values out when asked), so the client has
 * to look again by itself. One timer serves every source: it waits for the nearest end, tells the sources whose time
 * passed, and waits for the next. No timer runs while nothing is watched or nothing is due to end.
 */
export function createExpiryClock(now: () => number = () => Date.now()): ExpiryClock {
  const sources = new Map<string, Source>();
  let timer: ReturnType<typeof setTimeout> | undefined;

  const schedule = () => {
    if (timer !== undefined) {
      clearTimeout(timer);
      timer = undefined;
    }
    let nearest = Number.POSITIVE_INFINITY;
    for (const source of sources.values()) {
      for (const time of source.times) nearest = Math.min(nearest, time);
    }
    if (nearest === Number.POSITIVE_INFINITY) return;
    // A far end is reached in steps of the longest delay; a timer that fires a moment early simply waits again.
    timer = setTimeout(fire, Math.min(Math.max(nearest - now(), 1), MAX_TIMER_MS));
  };

  const fire = () => {
    timer = undefined;
    const at = now();
    const due: (() => void)[] = [];
    for (const source of sources.values()) {
      const left = source.times.filter((time) => time > at);
      if (left.length !== source.times.length) {
        source.times = left;
        due.push(...source.listeners);
      }
    }
    schedule();
    for (const listener of due) listener();
  };

  return {
    watch: (key, times, listener) => {
      const at = now();
      const future: number[] = [];
      for (const time of times) {
        const value = time ? Date.parse(time) : Number.NaN;
        if (value > at) future.push(value);
      }
      const source = sources.get(key) ?? { times: [], listeners: new Set() };
      source.times = future;
      source.listeners.add(listener);
      sources.set(key, source);
      schedule();
      return () => {
        source.listeners.delete(listener);
        if (source.listeners.size === 0 && sources.get(key) === source) sources.delete(key);
        schedule();
      };
    },
  };
}

/** The clock every view of this window shares, so there is one timer however many people are shown. */
export const expiryClock = createExpiryClock();
