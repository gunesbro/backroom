import { EVENT_NAMES, type ConnectionStatus, type RealtimeEventName, type RealtimeEvents } from './events';
import { createSignalrHub, type Hub, type HubFactory } from './hub';

export type RealtimeListener<K extends RealtimeEventName> = (payload: RealtimeEvents[K]) => void;

/** One cell's live connection. Subscriptions survive reconnects: they are replayed after every connect. */
export interface RealtimeClient {
  /** Connects (and keeps reconnecting) until `stop`. Calling it again has no effect. */
  start: () => void;
  stop: () => Promise<void>;
  subscribeOrganization: (orgId: string) => void;
  unsubscribeOrganization: (orgId: string) => void;
  subscribeChannel: (orgId: string, channelId: string) => void;
  unsubscribeChannel: (orgId: string, channelId: string) => void;
  on: <K extends RealtimeEventName>(event: K, listener: RealtimeListener<K>) => () => void;
  /** Called on every status change; `reconnected` is true for a connect that follows a loss. */
  onStatus: (listener: (status: ConnectionStatus, reconnected: boolean) => void) => () => void;
  status: () => ConnectionStatus;
}

export type RealtimeOptions = {
  cellUrl: string;
  getAccessToken: () => Promise<string>;
  /** Tests inject a fake hub. */
  hubFactory?: HubFactory;
  /** Delay before reconnect attempt `n` (0-based), in ms; the default is exponential backoff with full jitter. */
  retryDelay?: (attempt: number) => number;
  /** Replaces `setTimeout`, for tests. Returns a function that cancels the call. */
  schedule?: (callback: () => void, ms: number) => () => void;
};

const BASE_DELAY_MS = 500;
const MAX_DELAY_MS = 30_000;

/** Exponential backoff capped at 30 s, with full jitter so a restarted node is not hit by every client at once. */
export function backoffWithJitter(attempt: number, random: () => number = Math.random): number {
  const ceiling = Math.min(MAX_DELAY_MS, BASE_DELAY_MS * 2 ** Math.min(attempt, 10));
  return Math.floor(random() * ceiling);
}

const defaultSchedule = (callback: () => void, ms: number) => {
  const timer = setTimeout(callback, ms);
  return () => {
    clearTimeout(timer);
  };
};

type Join = { method: string; args: string[] };

export function createRealtimeClient(options: RealtimeOptions): RealtimeClient {
  const hubFactory = options.hubFactory ?? createSignalrHub;
  const retryDelay = options.retryDelay ?? ((attempt: number) => backoffWithJitter(attempt));
  const schedule = options.schedule ?? defaultSchedule;
  const url = `${options.cellUrl.replace(/\/+$/, '')}/hubs/realtime`;

  const listeners: { [K in RealtimeEventName]: Set<RealtimeListener<K>> } = {
    MessagePosted: new Set(),
    ChannelCreated: new Set(),
    SessionRevoked: new Set(),
  };
  const statusListeners = new Set<(status: ConnectionStatus, reconnected: boolean) => void>();
  // The groups to be in, as hub method plus arguments; replayed after every connect.
  const wanted = new Map<string, Join>();

  let status: ConnectionStatus = 'stopped';
  let started = false;
  let hub: Hub | undefined;
  let cancelRetry: (() => void) | undefined;
  let attempt = 0;
  let everConnected = false;
  // The hub handles one invocation at a time per client, so they go one after the other.
  let queue: Promise<unknown> = Promise.resolve();

  const setStatus = (next: ConnectionStatus, reconnected = false) => {
    status = next;
    for (const listener of [...statusListeners]) listener(next, reconnected);
  };

  const invoke = (target: Hub, key: string, { method, args }: Join): void => {
    const run = queue.then(() => target.invoke(method, ...args));
    queue = run.catch(() => undefined);
    run.catch((error: unknown) => {
      // Refused for every reason the same way (not a member, unknown or non-public channel): do not ask again.
      if (isHubError(error, 'realtime.not_found') && method.startsWith('Subscribe')) wanted.delete(key);
    });
  };

  const join = (key: string, method: string, args: string[]) => {
    wanted.set(key, { method, args });
    if (status === 'connected' && hub) invoke(hub, key, { method, args });
  };
  const leave = (key: string, method: string, args: string[]) => {
    const had = wanted.delete(key);
    if (had && status === 'connected' && hub) invoke(hub, key, { method, args });
  };

  const scheduleRetry = () => {
    cancelRetry?.();
    const delay = retryDelay(attempt);
    attempt += 1;
    cancelRetry = schedule(() => {
      void connect();
    }, delay);
  };

  const isStarted = () => started;
  const isCurrent = (candidate: Hub) => started && hub === candidate;

  const connect = async (): Promise<void> => {
    cancelRetry = undefined;
    if (!started) return;
    const reconnected = everConnected;
    let current: Hub | undefined;
    try {
      const created = await hubFactory(url, options.getAccessToken);
      current = created;
      for (const name of EVENT_NAMES) {
        created.on(name, (payload) => {
          if (created !== hub) return;
          if (name === 'SessionRevoked') {
            // The server closes the connection next; the session is over, so never reconnect.
            started = false;
          }
          for (const listener of [...listeners[name]] as ((payload: unknown) => void)[]) listener(payload);
        });
      }
      created.onClose(() => {
        if (created !== hub) return;
        hub = undefined;
        if (!started) {
          setStatus('stopped');
          return;
        }
        setStatus('reconnecting');
        scheduleRetry();
      });
      hub = created;
      await created.start();
      if (!isCurrent(created)) {
        await created.stop().catch(() => undefined);
        return;
      }
    } catch {
      if (current && hub === current) hub = undefined;
      if (isStarted()) {
        if (everConnected) setStatus('reconnecting');
        scheduleRetry();
      }
      return;
    }
    attempt = 0;
    everConnected = true;
    setStatus('connected', reconnected);
    for (const [key, entry] of [...wanted]) invoke(current, key, entry);
  };

  return {
    start: () => {
      if (started) return;
      started = true;
      setStatus('connecting');
      void connect();
    },
    stop: async () => {
      started = false;
      cancelRetry?.();
      cancelRetry = undefined;
      const current = hub;
      hub = undefined;
      setStatus('stopped');
      if (current) await current.stop().catch(() => undefined);
    },
    subscribeOrganization: (orgId) => {
      join(`org:${orgId}`, 'SubscribeOrganization', [orgId]);
    },
    unsubscribeOrganization: (orgId) => {
      leave(`org:${orgId}`, 'UnsubscribeOrganization', [orgId]);
    },
    subscribeChannel: (orgId, channelId) => {
      join(`channel:${orgId}/${channelId}`, 'SubscribeChannel', [orgId, channelId]);
    },
    unsubscribeChannel: (orgId, channelId) => {
      leave(`channel:${orgId}/${channelId}`, 'UnsubscribeChannel', [orgId, channelId]);
    },
    on: (event, listener) => {
      const set = listeners[event] as Set<typeof listener>;
      set.add(listener);
      return () => {
        set.delete(listener);
      };
    },
    onStatus: (listener) => {
      statusListeners.add(listener);
      return () => {
        statusListeners.delete(listener);
      };
    },
    status: () => status,
  };
}

function isHubError(error: unknown, code: string): boolean {
  return error instanceof Error && error.message.includes(code);
}
