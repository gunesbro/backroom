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
import { applyChannelCreated, applyLiveMessage, catchUpChannel } from './live-cache';
import { channelsKey } from './queries';
import { RealtimeContext } from './realtime-context';
import type { Cell, MessagingApi } from './api';

/**
 * Channel groups joined at most: the hub allows 60 subscribe calls per 10 s per connection, and one is the
 * organization. The selected channel always counts; the rest are the first ones in list order.
 */
export const MAX_CHANNEL_SUBSCRIPTIONS = 50;

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
 * the background, and signs out when the session is revoked.
 */
export function useLiveUpdates({
  cell,
  api,
  orgId,
  channelIds,
  selectedId,
  ownAccountId,
}: {
  cell: Cell;
  api: MessagingApi;
  orgId: string;
  channelIds: readonly string[];
  selectedId: string | undefined;
  ownAccountId: string | undefined;
}) {
  const client = useRealtimeClient(cell);
  const queryClient = useQueryClient();
  const { signOut } = useAuth();
  const [activity, setActivity] = useState<ReadonlySet<string>>(NO_ACTIVITY);

  const status = useSyncExternalStore<ConnectionStatus>(
    useCallback((notify) => client?.onStatus(notify) ?? (() => undefined), [client]),
    useCallback(() => client?.status() ?? 'stopped', [client]),
  );

  // Handlers read the latest values through a ref, so they are registered once per connection.
  const latest = useRef({ api, orgId, selectedId, ownAccountId, signOut });
  useLayoutEffect(() => {
    latest.current = { api, orgId, selectedId, ownAccountId, signOut };
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
        client.subscribeChannel(orgId, id);
        joined.current.add(id);
      }
    }
    for (const id of [...joined.current]) {
      if (!want.has(id)) {
        client.unsubscribeChannel(orgId, id);
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
    let catching = false;
    const catchUp = () => {
      const current = latest.current;
      void queryClient.invalidateQueries({ queryKey: channelsKey(current.orgId) });
      if (!current.selectedId || catching) return;
      catching = true;
      void catchUpChannel(current.api, queryClient, current.orgId, current.selectedId).finally(() => {
        catching = false;
      });
    };

    const offs = [
      client.on('MessagePosted', ({ orgId: eventOrg, channelId, message }) => {
        const current = latest.current;
        if (eventOrg !== current.orgId) return;
        if (channelId === current.selectedId) {
          applyLiveMessage(queryClient, eventOrg, channelId, message);
        } else if (message.authorAccountId !== current.ownAccountId) {
          setActivity((previous) => (previous.has(channelId) ? previous : new Set(previous).add(channelId)));
        }
      }),
      client.on('ChannelCreated', ({ orgId: eventOrg, channel }) => {
        if (eventOrg === latest.current.orgId) applyChannelCreated(queryClient, eventOrg, channel);
      }),
      client.on('SessionRevoked', () => {
        void latest.current.signOut();
      }),
      client.onStatus((next, reconnected) => {
        if (next === 'connected' && reconnected) catchUp();
      }),
    ];

    let hidden = document.visibilityState === 'hidden';
    const onVisibility = () => {
      if (document.visibilityState === 'hidden') {
        hidden = true;
      } else if (hidden) {
        hidden = false;
        catchUp();
      }
    };
    document.addEventListener('visibilitychange', onVisibility);
    return () => {
      document.removeEventListener('visibilitychange', onVisibility);
      for (const off of offs) off();
    };
  }, [client, queryClient]);

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
