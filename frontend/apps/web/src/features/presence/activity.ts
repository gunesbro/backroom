import type { ActivityState } from '@backroom/realtime';

/** Idle after this long without input (requirements: "boşta (10 dakika hareketsizlik)"). */
export const IDLE_AFTER_MS = 10 * 60_000;

/** The hub takes one `ReportActivity` per 10 s per connection and drops the rest. */
export const REPORT_GAP_MS = 10_000;

/** While input goes on, `active` is sent again at most this often, so the cell's 10-minute idle never fires on a busy person. */
export const ACTIVE_REFRESH_MS = 60_000;

export type Timers = {
  now: () => number;
  setTimer: (callback: () => void, ms: number) => unknown;
  clearTimer: (handle: unknown) => void;
};

const browserTimers: Timers = {
  now: () => Date.now(),
  setTimer: (callback, ms) => setTimeout(callback, ms),
  clearTimer: (handle) => {
    clearTimeout(handle as ReturnType<typeof setTimeout>);
  },
};

type ActivityEvent = 'input' | 'change';

export interface ActivityMonitor {
  state: () => ActivityState;
  /** Starts listening with the first subscriber and stops with the last. */
  subscribe: (listener: (event: ActivityEvent) => void) => () => void;
}

const INPUT_EVENTS = ['pointerdown', 'pointermove', 'keydown', 'wheel', 'touchstart', 'focus'] as const;

/**
 * Whether the person uses this window: input (pointer, keys, wheel, touch, focus, the page becoming visible) makes it
 * active, 10 minutes without input idle. One timer, set for the moment the quiet would reach the limit, never reset
 * on every input.
 */
export function createActivityMonitor(
  target: Pick<Window, 'addEventListener' | 'removeEventListener'> = window,
  doc: Pick<Document, 'addEventListener' | 'removeEventListener' | 'visibilityState'> = document,
  timers: Timers = browserTimers,
): ActivityMonitor {
  const listeners = new Set<(event: ActivityEvent) => void>();
  let state: ActivityState = 'active';
  let lastInput = 0;
  let timer: unknown;

  const emit = (event: ActivityEvent) => {
    for (const listener of [...listeners]) listener(event);
  };
  const arm = (delay: number) => {
    if (timer !== undefined) return;
    timer = timers.setTimer(check, delay);
  };
  function check() {
    timer = undefined;
    const quiet = timers.now() - lastInput;
    if (quiet >= IDLE_AFTER_MS) {
      if (state !== 'idle') {
        state = 'idle';
        emit('change');
      }
      return;
    }
    arm(IDLE_AFTER_MS - quiet);
  }
  const input = () => {
    lastInput = timers.now();
    if (state === 'idle') {
      state = 'active';
      emit('change');
    }
    emit('input');
    arm(IDLE_AFTER_MS);
  };
  const visibility = () => {
    if (doc.visibilityState === 'visible') input();
  };
  const options = { capture: true, passive: true } as const;

  const start = () => {
    state = 'active';
    lastInput = timers.now();
    for (const name of INPUT_EVENTS) target.addEventListener(name, input, options);
    doc.addEventListener('visibilitychange', visibility);
    arm(IDLE_AFTER_MS);
  };
  const stop = () => {
    for (const name of INPUT_EVENTS) target.removeEventListener(name, input, options);
    doc.removeEventListener('visibilitychange', visibility);
    if (timer !== undefined) timers.clearTimer(timer);
    timer = undefined;
  };

  return {
    state: () => state,
    subscribe: (listener) => {
      if (listeners.size === 0) start();
      listeners.add(listener);
      return () => {
        listeners.delete(listener);
        if (listeners.size === 0) stop();
      };
    },
  };
}

export interface ActivityReporter {
  /** The person did something. */
  input: () => void;
  /** The local state changed (idle after the quiet, active again). */
  change: (state: ActivityState) => void;
  /** A new connection: the cell counts it as active from now on; `current` is what the person really is. */
  connected: (current: ActivityState) => void;
  dispose: () => void;
}

/**
 * Sends the latest activity state at most once per {@link REPORT_GAP_MS} (a call too soon is held and the newest
 * state sent when the gap is over), and while the person stays active again at most once per {@link ACTIVE_REFRESH_MS}.
 */
export function createActivityReporter(
  send: (state: ActivityState) => void,
  timers: Timers = browserTimers,
): ActivityReporter {
  let desired: ActivityState = 'active';
  let sent: ActivityState = 'active';
  let lastSentAt = Number.NEGATIVE_INFINITY;
  let lastActiveAt = timers.now();
  let timer: unknown;

  const refreshDue = () => desired === 'active' && timers.now() - lastActiveAt >= ACTIVE_REFRESH_MS;
  const flush = () => {
    if (desired === sent && !refreshDue()) return;
    const wait = lastSentAt + REPORT_GAP_MS - timers.now();
    if (wait > 0) {
      timer ??= timers.setTimer(() => {
        timer = undefined;
        flush();
      }, wait);
      return;
    }
    send(desired);
    sent = desired;
    lastSentAt = timers.now();
    if (desired === 'active') lastActiveAt = lastSentAt;
  };

  return {
    input: () => {
      desired = 'active';
      if (sent !== 'active' || refreshDue()) flush();
    },
    change: (state) => {
      desired = state;
      flush();
    },
    connected: (current) => {
      sent = 'active';
      lastActiveAt = timers.now();
      lastSentAt = Number.NEGATIVE_INFINITY;
      desired = current;
      flush();
    },
    dispose: () => {
      if (timer !== undefined) timers.clearTimer(timer);
      timer = undefined;
    },
  };
}

let shared: ActivityMonitor | undefined;

/** This window's monitor, shared by the status dot and every cell's reporter. */
export function activityMonitor(): ActivityMonitor {
  shared ??= createActivityMonitor();
  return shared;
}
