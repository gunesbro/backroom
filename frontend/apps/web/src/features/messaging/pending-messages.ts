import { useCallback, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { ApiError } from '@/shared/http';
import { addMessageToCache, messagesKey } from './queries';
import type { MessagingApi } from './api';

/** A message the person sent that the server has not confirmed yet. */
export type PendingMessage = {
  /** Chosen once per message: a retry sends the same id, so the server never stores a message twice. */
  clientMessageId: string;
  text: string;
  createdAt: string;
  status: 'sending' | 'failed';
  /** The error code of a failed send. */
  code?: string | undefined;
};

type ByChannel = Readonly<Record<string, readonly PendingMessage[]>>;

const NONE: readonly PendingMessage[] = [];

/** Optimistic sending for one organization: pending messages per channel, replaced by the server's message. */
export function usePendingMessages(api: MessagingApi, orgId: string) {
  const queryClient = useQueryClient();
  const [byChannel, setByChannel] = useState<ByChannel>({});

  const update = useCallback(
    (channelId: string, change: (list: readonly PendingMessage[]) => readonly PendingMessage[]) => {
      setByChannel((current) => ({ ...current, [channelId]: change(current[channelId] ?? NONE) }));
    },
    [],
  );

  const deliver = useCallback(
    async (channelId: string, pending: PendingMessage) => {
      update(channelId, (list) =>
        list.map((item) =>
          item.clientMessageId === pending.clientMessageId
            ? { ...item, status: 'sending', code: undefined }
            : item,
        ),
      );
      try {
        const message = await api.sendMessage(orgId, channelId, {
          clientMessageId: pending.clientMessageId,
          text: pending.text,
        });
        addMessageToCache(queryClient, orgId, channelId, message);
        update(channelId, (list) => list.filter((item) => item.clientMessageId !== pending.clientMessageId));
        // Not loaded yet (the person typed first): load it, so the message is not missing.
        if (!queryClient.getQueryData(messagesKey(orgId, channelId))) {
          void queryClient.invalidateQueries({ queryKey: messagesKey(orgId, channelId) });
        }
      } catch (error) {
        const code = error instanceof ApiError ? error.code : 'client.failed';
        update(channelId, (list) =>
          list.map((item) =>
            item.clientMessageId === pending.clientMessageId ? { ...item, status: 'failed', code } : item,
          ),
        );
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
      const pending = byChannel[channelId]?.find((item) => item.clientMessageId === clientMessageId);
      if (pending?.status === 'failed') void deliver(channelId, pending);
    },
    [byChannel, deliver],
  );

  const discard = useCallback(
    (channelId: string, clientMessageId: string) => {
      update(channelId, (list) => list.filter((item) => item.clientMessageId !== clientMessageId));
    },
    [update],
  );

  const forChannel = useCallback((channelId: string) => byChannel[channelId] ?? NONE, [byChannel]);

  return { forChannel, send, retry, discard };
}
