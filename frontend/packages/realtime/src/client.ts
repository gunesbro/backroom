import { EVENT_NAMES, type ConnectionStatus, type RealtimeEventName, type RealtimeEvents } from './events';
import { createSignalrHub, type Hub, type HubFactory } from './hub';

export type RealtimeListener<K extends RealtimeEventName> = (payload: RealtimeEvents[K]) => void;

/** One cell's live connection. Subscriptions survive reconnects: they are replayed after every connect. */
export interface RealtimeClient {
  /** Connects (and keeps reconnecting) until `stop`. Calling it again has no effect. */
  start: () => void;
  stop: () => Promise<void>;
  /** Joins the organization's group and the account's own group in it (per-person events such as read state). */
  subscribeOrganization: (orgId: string) => void;
  unsubscribeOrganization: (orgId: string) => void;
  /** Any conversation the account may read (channel, private channel, DM, group DM); threads use the parent's group. */
  subscribeConversation: (orgId: string, conversationId: string) => void;
  unsubscribeConversation: (orgId: string, conversationId: string) => void;
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
  // The hub lives at the cell's origin; URL parsing (not a regular expression) drops any path or trailing slashes.
  const url = new URL('/hubs/realtime', options.cellUrl).href;

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
  // Bumped by every start and stop: a connect attempt overtaken while its hub was still being built (stop, then
  // start, as React StrictMode does) must not open a second connection next to the new attempt.
  let generation = 0;
  let hub: Hub | undefined;
  let cancelRetry: (() => void) | undefined;
  let attempt = 0;
  let everConnected = false;
  // The hub is started and the replay of the groups has begun: new joins go straight to it.
  let ready = false;
  // The hub handles one invocation at a time per client, so they go one after the other.
  let queue: Promise<unknown> = Promise.resolve();

  const setStatus = (next: ConnectionStatus, reconnected = false) => {
    status = next;
    for (const listener of [...statusListeners]) listener(next, reconnected);
  };

  /** Resolves when the hub answered (or refused); never rejects. */
  const invoke = (target: Hub, key: string, { method, args }: Join): Promise<void> => {
    const run = queue.then(() => {
      // Dropped since it was queued (its organization was refused): do not ask, a refusal counts against the limit.
      if (method.startsWith('Subscribe') && !wanted.has(key)) return undefined;
      return target.invoke(method, ...args);
    });
    queue = run.catch(() => undefined);
    return run.then(
      () => undefined,
      (error: unknown) => {
        // Refused for every reason the same way (not a member, unknown conversation, or one it may not read): do not ask again.
        // A refused organization takes its conversations with it, or every reconnect would run into the refusal limit.
        if (method.startsWith('Subscribe') && isHubError(error, 'realtime.not_found')) {
          wanted.delete(key);
          if (method === 'SubscribeOrganization') {
            for (const other of [...wanted.keys()]) {
              if (other.startsWith(`conversation:${args[0] ?? ''}/`)) wanted.delete(other);
            }
          }
        }
      },
    );
  };

  const join = (key: string, method: string, args: string[]) => {
    wanted.set(key, { method, args });
    if (ready && hub) void invoke(hub, key, { method, args });
  };
  const leave = (key: string, method: string, args: string[]) => {
    const had = wanted.delete(key);
    if (had && ready && hub) void invoke(hub, key, { method, args });
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
    const mine = generation;
    const reconnected = everConnected;
    let current: Hub | undefined;
    try {
      const created = await hubFactory(url, options.getAccessToken);
      if (mine !== generation) {
        // Overtaken while the hub was being built: it was never started, release it.
        await created.stop().catch(() => undefined);
        return;
      }
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
        ready = false;
        if (!isStarted()) {
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
      // An overtaken attempt fails because stop aborted it; the newer attempt owns the connection and the retries.
      if (mine !== generation) return;
      if (current && hub === current) hub = undefined;
      if (isStarted()) {
        if (everConnected) setStatus('reconnecting');
        scheduleRetry();
      }
      return;
    }
    attempt = 0;
    everConnected = true;
    ready = true;
    // Connected means in the groups: a catch-up that starts earlier could miss what is posted before the join.
    await Promise.all([...wanted].map(([key, entry]) => invoke(current, key, entry)));
    if (!isCurrent(current)) return;
    setStatus('connected', reconnected);
  };

  return {
    start: () => {
      if (started) return;
      started = true;
      generation += 1;
      setStatus('connecting');
      void connect();
    },
    stop: async () => {
      started = false;
      generation += 1;
      cancelRetry?.();
      cancelRetry = undefined;
      const current = hub;
      hub = undefined;
      ready = false;
      setStatus('stopped');
      if (current) await current.stop().catch(() => undefined);
    },
    subscribeOrganization: (orgId) => {
      join(`org:${orgId}`, 'SubscribeOrganization', [orgId]);
    },
    unsubscribeOrganization: (orgId) => {
      leave(`org:${orgId}`, 'UnsubscribeOrganization', [orgId]);
    },
    subscribeConversation: (orgId, conversationId) => {
      join(`conversation:${orgId}/${conversationId}`, 'SubscribeConversation', [orgId, conversationId]);
    },
    unsubscribeConversation: (orgId, conversationId) => {
      leave(`conversation:${orgId}/${conversationId}`, 'UnsubscribeConversation', [orgId, conversationId]);
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
