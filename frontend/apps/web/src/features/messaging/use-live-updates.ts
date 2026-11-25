import {
  useCallback,
  useContext,
  useEffect,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
  useSyncExternalStore,
} from 'react';
import { useQueryClient } from '@tanstack/react-query';
import type { ConnectionStatus, RealtimeClient } from '@backroom/realtime';
import { useAuth } from '@/features/auth';
import { useActivityReporting } from '@/features/presence';
import {
  applyConversationUpdated,
  applyLiveMessage,
  catchUpChannel,
  forgetConversation,
  noteChannelCreated,
} from './live-cache';
import { browseChannelsKey, conversationsKey } from './queries';
import { RealtimeContext } from './realtime-context';
import type { Cell, MessagingApi } from './api';

/**
 * Channel groups joined at most: the hub allows 60 subscribe calls per 10 s per connection, and one is the
 * organization. The selected channel always counts; the rest are the first ones in list order.
 */
export const MAX_CHANNEL_SUBSCRIPTIONS = 50;

/** The channel list is refreshed after the tab was in the background at least this long. */
const REFRESH_CHANNELS_AFTER_MS = 30_000;

const NO_ACTIVITY: ReadonlySet<string> = new Set();

/** The cell's live connection for as long as the workspace is open; null while live updates are off. */
function useRealtimeClient(cell: Cell): RealtimeClient | null {
  const factory = useContext(RealtimeContext);
  const client = useMemo(
    () => (factory ? factory(cell) : null),
    // The cell's id and address identify it; the object itself may be new on every render.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [factory, cell.id, cell.url],
  );
  useEffect(() => {
    if (!client) return;
    client.start();
    return () => {
      void client.stop();
    };
  }, [client]);
  return client;
}

/**
 * Live updates of one organization (Phase 1, step 7b): puts live messages of the open channel into the cache,
 * marks activity in other channels, adds created channels, catches up after a reconnect or after the tab was in
 * the background, and signs out when the session is revoked. Phase 2, step 7: a conversation the person was added
 * to appears, one they were removed from goes away with everything cached about it, and a changed conversation
 * (name, description, archive state, member count) is corrected in place.
 */
export function useLiveUpdates({
  cell,
  api,
  orgId,
  channelIds,
  selectedId,
  ownAccountId,
  detailedId,
  onRemoved,
}: {
  cell: Cell;
  api: MessagingApi;
  orgId: string;
  channelIds: readonly string[];
  selectedId: string | undefined;
  ownAccountId: string | undefined;
  /** The conversation whose owner and members are on screen (its info is open): changes to it are read again. */
  detailedId?: string | undefined;
  /** The person's membership of a conversation ended (they left, or were removed). */
  onRemoved?: (conversationId: string) => void;
}) {
  const client = useRealtimeClient(cell);
  // Idle after 10 minutes without input, active again on input (presence, Phase 2 flow 9); off with its flag.
  useActivityReporting(client);
  const queryClient = useQueryClient();
  const { signOut } = useAuth();
  const catchUpRef = useRef<(() => void) | undefined>(undefined);
  const [activity, setActivity] = useState<ReadonlySet<string>>(NO_ACTIVITY);

  const status = useSyncExternalStore<ConnectionStatus>(
    useCallback((notify) => client?.onStatus(notify) ?? (() => undefined), [client]),
    useCallback(() => client?.status() ?? 'stopped', [client]),
  );

  // Handlers read the latest values through a ref, so they are registered once per connection.
  const latest = useRef({ api, orgId, selectedId, ownAccountId, signOut, detailedId, onRemoved });
  useLayoutEffect(() => {
    latest.current = { api, orgId, selectedId, ownAccountId, signOut, detailedId, onRemoved };
  });

  // Groups: the organization, and the channels (a diff, never a full re-subscribe: the hub rate-limits it).
  useEffect(() => {
    if (!client) return;
    client.subscribeOrganization(orgId);
    return () => {
      client.unsubscribeOrganization(orgId);
    };
  }, [client, orgId]);

  const wanted = useMemo(() => {
    const ids = new Set(selectedId ? [selectedId] : []);
    for (const id of channelIds) {
      if (ids.size >= MAX_CHANNEL_SUBSCRIPTIONS) break;
      ids.add(id);
    }
    return [...ids].sort().join(',');
  }, [channelIds, selectedId]);
  const joined = useRef(new Set<string>());
  useEffect(() => {
    if (!client) return;
    const want = new Set(wanted === '' ? [] : wanted.split(','));
    for (const id of want) {
      if (!joined.current.has(id)) {
        client.subscribeConversation(orgId, id);
        joined.current.add(id);
      }
    }
    for (const id of [...joined.current]) {
      if (!want.has(id)) {
        client.unsubscribeConversation(orgId, id);
        joined.current.delete(id);
      }
    }
  }, [client, orgId, wanted]);
  // A new connection or organization starts with no groups.
  useEffect(() => {
    const set = joined.current;
    return () => {
      set.clear();
    };
  }, [client, orgId]);

  useEffect(() => {
    if (!client) return;
    // One catch-up at a time; a trigger during a run supersedes it (the run stops) and starts another.
    let running = false;
    let rerun = false;
    let disposed = false;
    let controller: AbortController | undefined;
    const shouldRerun = () => rerun && !disposed;
    const run = async () => {
      running = true;
      do {
        rerun = false;
        controller = new AbortController();
        const current = latest.current;
        if (current.selectedId) {
          await catchUpChannel(
            current.api,
            queryClient,
            current.orgId,
            current.selectedId,
            controller.signal,
          );
        }
      } while (shouldRerun());
      running = false;
    };
    const catchUp = (refreshChannels: boolean) => {
      if (refreshChannels) {
        void queryClient.invalidateQueries({ queryKey: conversationsKey(latest.current.orgId) });
        void queryClient.invalidateQueries({ queryKey: browseChannelsKey(latest.current.orgId) });
      }
      if (running) {
        rerun = true;
        controller?.abort();
      } else {
        void run();
      }
    };
    catchUpRef.current = () => {
      catchUp(false);
    };

    let revoked = false;
    const offs = [
      client.on('MessagePosted', ({ orgId: eventOrg, channelId, message }) => {
        const current = latest.current;
        if (eventOrg !== current.orgId) return;
        // Every cached channel is kept current (nothing happens for one that is not cached).
        applyLiveMessage(queryClient, eventOrg, channelId, message);
        if (channelId !== current.selectedId && message.authorAccountId !== current.ownAccountId) {
          setActivity((previous) => (previous.has(channelId) ? previous : new Set(previous).add(channelId)));
        }
      }),
      client.on('ChannelCreated', ({ orgId: eventOrg }) => {
        if (eventOrg === latest.current.orgId) noteChannelCreated(queryClient, eventOrg);
      }),
      client.on('MembershipChanged', ({ orgId: eventOrg, conversationId, change }) => {
        const current = latest.current;
        if (eventOrg !== current.orgId) return;
        if (change === 'added') {
          void queryClient.invalidateQueries({ queryKey: conversationsKey(eventOrg) });
          return;
        }

        // The connection has already dropped the group; a later membership must join it again.
        joined.current.delete(conversationId);
        current.onRemoved?.(conversationId);
        forgetConversation(queryClient, eventOrg, conversationId);
      }),
      client.on('ConversationUpdated', (event) => {
        const current = latest.current;
        if (event.orgId !== current.orgId) return;
        applyConversationUpdated(queryClient, event, event.conversationId === current.detailedId);
      }),
      client.on('SessionRevoked', () => {
        if (revoked) return;
        revoked = true;
        void latest.current.signOut();
      }),
      client.onStatus((next, reconnected) => {
        if (next === 'connected' && reconnected) catchUp(true);
      }),
    ];

    let hiddenAt: number | undefined = document.visibilityState === 'hidden' ? Date.now() : undefined;
    const onVisibility = () => {
      if (document.visibilityState === 'hidden') {
        hiddenAt ??= Date.now();
      } else if (hiddenAt !== undefined) {
        const away = Date.now() - hiddenAt;
        hiddenAt = undefined;
        catchUp(away >= REFRESH_CHANNELS_AFTER_MS);
      }
    };
    document.addEventListener('visibilitychange', onVisibility);
    return () => {
      disposed = true;
      controller?.abort();
      catchUpRef.current = undefined;
      document.removeEventListener('visibilitychange', onVisibility);
      for (const off of offs) off();
    };
  }, [client, queryClient]);

  // A cached channel may have missed events while it was not open: catch up when it is selected.
  useEffect(() => {
    if (selectedId) catchUpRef.current?.();
  }, [selectedId, client]);

  const clearActivity = useCallback((channelId: string) => {
    setActivity((previous) => {
      if (!previous.has(channelId)) return previous;
      const next = new Set(previous);
      next.delete(channelId);
      return next;
    });
  }, []);

  return { status, activity, clearActivity, live: client !== null };
}
