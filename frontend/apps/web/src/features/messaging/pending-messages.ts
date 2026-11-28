import { useCallback, useLayoutEffect, useRef, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { ApiError } from '@/shared/http';
import { applyLiveMessage } from './live-cache';
import { messagesKey } from './queries';
import type { Message, MessageQuote, MessagingApi } from './api';
import type { Block } from './rich-text/blocks';

/**
 * A message the person sent that is not in a fetched page yet. `confirmed` ones have the server's message and stay
 * until a fetched page contains it, so a refetch that started before the answer cannot make it vanish.
 */
export type PendingMessage = {
  /** Chosen once per message: a retry sends the same id, so the server never stores a message twice. */
  clientMessageId: string;
  text: string;
  /** The rich text blocks, when the message was written in the rich text composer. */
  blocks?: Block[] | undefined;
  /** The quote of the message this one replies to, as it was when the person sent it (drawn until the server answers). */
  replyTo?: MessageQuote | undefined;
  createdAt: string;
  status: 'sending' | 'failed' | 'confirmed';
  /** The error code of a failed send. */
  code?: string | undefined;
  /** The server's `reason` token of a refused message (`messaging.message_invalid`, `messaging.mention_invalid`). */
  reason?: string | undefined;
  /** The server's message, once confirmed. */
  message?: Message | undefined;
};

type ByChannel = Readonly<Record<string, readonly PendingMessage[]>>;

const NONE: readonly PendingMessage[] = [];

/** Deleted originals remembered so that a reply started before the delete is known; the oldest go first. */
const MAX_DELETED_ORIGINALS = 1000;

const DELETED_REASONS: ReadonlySet<string> = new Set(['reply.deleted', 'reply.not_found']);

/** The quote of a deleted original: its id only, with no author and no text. */
const deletedQuote = (messageId: string): MessageQuote => ({ messageId, deleted: true });

/** The item with the quote of this original (its own and its confirmed message's) turned into a deleted one. */
function withDeletedOriginal(item: PendingMessage, messageId: string): PendingMessage {
  const own = item.replyTo?.messageId === messageId && !item.replyTo.deleted;
  const confirmed = item.message?.replyTo?.messageId === messageId && !item.message.replyTo.deleted;
  if (!own && !confirmed) return item;
  return {
    ...item,
    ...(own ? { replyTo: deletedQuote(messageId) } : {}),
    ...(confirmed && item.message ? { message: { ...item.message, replyTo: deletedQuote(messageId) } } : {}),
  };
}

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
  // Originals known to be deleted: a send that still quotes one carries no author or text of it.
  const deletedOriginals = useRef(new Set<string>());

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
      patch({ status: 'sending', code: undefined, reason: undefined });
      try {
        const message = await api.sendMessage(orgId, channelId, {
          clientMessageId: pending.clientMessageId,
          text: pending.text,
          ...(pending.blocks ? { blocks: pending.blocks } : {}),
          ...(pending.replyTo ? { replyToMessageId: pending.replyTo.messageId } : {}),
        });
        // A fetch that started before the answer could return a page without the message and overwrite it.
        await queryClient.cancelQueries({ queryKey: messagesKey(orgId, channelId) });
        applyLiveMessage(queryClient, orgId, channelId, message);
        // Sent before the first page arrived: the cancel left that load unfinished, so load it again.
        if (!queryClient.getQueryData(messagesKey(orgId, channelId))) {
          void queryClient.invalidateQueries({ queryKey: messagesKey(orgId, channelId) });
        }
        patch({ status: 'confirmed', message });
      } catch (error) {
        const reason = error instanceof ApiError ? error.reason : undefined;
        patch({
          status: 'failed',
          code: error instanceof ApiError ? error.code : 'client.failed',
          reason,
          // The original is gone: the row must not keep its author and text.
          ...(pending.replyTo &&
          error instanceof ApiError &&
          error.code === 'messaging.reply_invalid' &&
          reason !== undefined &&
          DELETED_REASONS.has(reason)
            ? { replyTo: deletedQuote(pending.replyTo.messageId) }
            : {}),
        });
      } finally {
        inFlight.current.delete(pending.clientMessageId);
      }
    },
    [api, orgId, queryClient, update],
  );

  /** `text` is already normalized (the plain text derived from `blocks`, when there are blocks). */
  const send = useCallback(
    (channelId: string, text: string, blocks?: Block[], replyTo?: MessageQuote) => {
      const known = replyTo !== undefined && deletedOriginals.current.has(replyTo.messageId);
      const pending: PendingMessage = {
        clientMessageId: crypto.randomUUID(),
        text,
        blocks,
        replyTo: known ? deletedQuote(replyTo.messageId) : replyTo,
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

  /**
   * A message was deleted: the quotes of unsent and just-sent replies to it (in any channel; ids are unique) lose its
   * author and text, as the cached replies do.
   */
  const markOriginalDeleted = useCallback((messageId: string) => {
    const deleted = deletedOriginals.current;
    deleted.delete(messageId);
    deleted.add(messageId);
    if (deleted.size > MAX_DELETED_ORIGINALS) {
      const oldest = deleted.values().next();
      if (!oldest.done) deleted.delete(oldest.value);
    }
    setByChannel((current) => {
      let changed = false;
      const next: Record<string, readonly PendingMessage[]> = {};
      for (const [channelId, list] of Object.entries(current)) {
        const mapped = list.map((item) => withDeletedOriginal(item, messageId));
        const same = mapped.every((item, index) => item === list[index]);
        if (!same) changed = true;
        next[channelId] = same ? list : mapped;
      }
      return changed ? next : current;
    });
  }, []);

  const forChannel = useCallback((channelId: string) => byChannel[channelId] ?? NONE, [byChannel]);

  return { forChannel, send, retry, discard, prune, markOriginalDeleted };
}
