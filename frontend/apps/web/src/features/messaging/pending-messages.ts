import { useCallback, useLayoutEffect, useRef, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { ApiError } from '@/shared/http';
import { addMessageToCache, messagesKey } from './queries';
import type { Message, MessagingApi } from './api';

/**
 * A message the person sent that is not in a fetched page yet. `confirmed` ones have the server's message and stay
 * until a fetched page contains it, so a refetch that started before the answer cannot make it vanish.
 */
export type PendingMessage = {
  /** Chosen once per message: a retry sends the same id, so the server never stores a message twice. */
  clientMessageId: string;
  text: string;
  createdAt: string;
  status: 'sending' | 'failed' | 'confirmed';
  /** The error code of a failed send. */
  code?: string | undefined;
  /** The server's message, once confirmed. */
  message?: Message | undefined;
};

type ByChannel = Readonly<Record<string, readonly PendingMessage[]>>;

const NONE: readonly PendingMessage[] = [];

/** Optimistic sending for one organization: pending messages per channel, replaced by the server's message. */
export function usePendingMessages(api: MessagingApi, orgId: string) {
  const queryClient = useQueryClient();
  const [byChannel, setByChannel] = useState<ByChannel>({});
  // Read by callbacks that must stay stable and must see the latest state.
  const latest = useRef(byChannel);
  useLayoutEffect(() => {
    latest.current = byChannel;
  });
  // Sends in flight, so a second click cannot post the same message twice before the state shows it.
  const inFlight = useRef(new Set<string>());

  const update = useCallback(
    (channelId: string, change: (list: readonly PendingMessage[]) => readonly PendingMessage[]) => {
      setByChannel((current) => {
        const list = change(current[channelId] ?? NONE);
        return list === (current[channelId] ?? NONE) ? current : { ...current, [channelId]: list };
      });
    },
    [],
  );

  const deliver = useCallback(
    async (channelId: string, pending: PendingMessage) => {
      if (inFlight.current.has(pending.clientMessageId)) return;
      inFlight.current.add(pending.clientMessageId);
      const patch = (change: Partial<PendingMessage>) => {
        update(channelId, (list) =>
          list.map((item) =>
            item.clientMessageId === pending.clientMessageId ? { ...item, ...change } : item,
          ),
        );
      };
      patch({ status: 'sending', code: undefined });
      try {
        const message = await api.sendMessage(orgId, channelId, {
          clientMessageId: pending.clientMessageId,
          text: pending.text,
        });
        // A fetch that started before the answer could return a page without the message and overwrite it.
        await queryClient.cancelQueries({ queryKey: messagesKey(orgId, channelId) });
        addMessageToCache(queryClient, orgId, channelId, message);
        patch({ status: 'confirmed', message });
      } catch (error) {
        patch({ status: 'failed', code: error instanceof ApiError ? error.code : 'client.failed' });
      } finally {
        inFlight.current.delete(pending.clientMessageId);
      }
    },
    [api, orgId, queryClient, update],
  );

  /** `text` is already normalized. */
  const send = useCallback(
    (channelId: string, text: string) => {
      const pending: PendingMessage = {
        clientMessageId: crypto.randomUUID(),
        text,
        createdAt: new Date().toISOString(),
        status: 'sending',
      };
      update(channelId, (list) => [...list, pending]);
      void deliver(channelId, pending);
    },
    [deliver, update],
  );

  const retry = useCallback(
    (channelId: string, clientMessageId: string) => {
      const pending = latest.current[channelId]?.find((item) => item.clientMessageId === clientMessageId);
      if (pending?.status === 'failed') void deliver(channelId, pending);
    },
    [deliver],
  );

  const discard = useCallback(
    (channelId: string, clientMessageId: string) => {
      update(channelId, (list) => list.filter((item) => item.clientMessageId !== clientMessageId));
    },
    [update],
  );

  /** Drops confirmed messages that a fetched page now contains. */
  const prune = useCallback(
    (channelId: string, fetchedIds: ReadonlySet<string>) => {
      update(channelId, (list) => {
        const kept = list.filter((item) => !(item.message && fetchedIds.has(item.message.id)));
        return kept.length === list.length ? list : kept;
      });
    },
    [update],
  );

  const forChannel = useCallback((channelId: string) => byChannel[channelId] ?? NONE, [byChannel]);

  return { forChannel, send, retry, discard, prune };
}
