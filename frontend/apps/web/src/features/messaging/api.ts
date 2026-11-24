import { createContext, useContext } from 'react';
import { requestJson, type AccessTokenSource } from '@/shared/http';
import type { Block } from './rich-text/blocks';

export type Channel = { id: string; name: string; createdAt: string };
export type ChannelPage = { channels: Channel[]; next: string | null };
export type Message = {
  id: string;
  /** The channel's sequence number: messages are ordered and paged by it, never by id or time. */
  seq: number;
  clientMessageId: string;
  authorAccountId: string;
  /** Plain text. The server also derives `blocks`; Phase 1 has one paragraph, so the text is all that is read. */
  text: string;
  /** The rich text blocks (docs/contracts/phase-2.md). Unchecked server data: read it with `parseBlocks`. */
  blocks?: unknown;
  createdAt: string;
};
/** Where a page starts: the newest messages (none), earlier than a seq, or later than a seq. */
export type MessageCursor = { before: number } | { after: number } | undefined;
export type MessagePage = { messages: Message[]; next: number | null };
export type Member = { accountId: string; displayName: string; email: string | null };
export type MemberPage = { members: Member[]; next: string | null };

/** Page size asked for; the server allows 1 to 100. */
export const PAGE_SIZE = 50;

/** A cell's channel and message endpoints (docs/contracts/phase-1.md, "HTTP APIs", Cell). */
export interface MessagingApi {
  listChannels: (orgId: string, after: string | undefined, signal?: AbortSignal) => Promise<ChannelPage>;
  createChannel: (orgId: string, name: string) => Promise<Channel>;
  /** Always newest first: `before` pages back (the `next` of the previous page), `after` pages forward. */
  listMessages: (
    orgId: string,
    channelId: string,
    cursor: MessageCursor,
    signal?: AbortSignal,
  ) => Promise<MessagePage>;
  /** Idempotent by `clientMessageId`: a repeat answers with the stored message. */
  sendMessage: (
    orgId: string,
    channelId: string,
    input: { clientMessageId: string; text: string; blocks?: Block[] },
  ) => Promise<Message>;
  /** One page of members, to put names to authors. */
  listMembers: (orgId: string, after: string | undefined, signal?: AbortSignal) => Promise<MemberPage>;
}

/** The cell's address is not one a token may be sent to. */
export class CellAddressError extends Error {
  constructor() {
    super('The cell address is not allowed.');
    this.name = 'CellAddressError';
  }
}

const LOOPBACK_HOSTS = new Set(['localhost', '127.0.0.1', '[::1]']);

/**
 * The origin of a cell address the control plane sent, or undefined if a token must not go there: https only, and
 * http only for a loopback host in a development build. Only the origin is used, never a path or query.
 */
export function cellOrigin(cellUrl: string, allowLoopbackHttp: boolean): string | undefined {
  let url: URL;
  try {
    url = new URL(cellUrl);
  } catch {
    return undefined;
  }
  if (url.username || url.password) return undefined;
  if (url.protocol === 'https:') return url.origin;
  if (url.protocol === 'http:' && allowLoopbackHttp && LOOPBACK_HOSTS.has(url.hostname)) return url.origin;
  return undefined;
}

export function createMessagingApi(
  cellUrl: string,
  cellToken: AccessTokenSource,
  { allowLoopbackHttp = import.meta.env.DEV }: { allowLoopbackHttp?: boolean } = {},
): MessagingApi {
  const origin = cellOrigin(cellUrl, allowLoopbackHttp);
  // Every request asks for its token first, so a refused address never receives one.
  const getAccessToken: AccessTokenSource = origin ? cellToken : () => Promise.reject(new CellAddressError());
  const org = (orgId: string) => `${origin ?? 'about:blank'}/api/v1/orgs/${encodeURIComponent(orgId)}`;
  const channel = (orgId: string, channelId: string) =>
    `${org(orgId)}/channels/${encodeURIComponent(channelId)}`;
  return {
    listChannels: (orgId, after, signal) => {
      const query = new URLSearchParams({ limit: String(PAGE_SIZE) });
      if (after) query.set('after', after);
      return requestJson(`${org(orgId)}/channels?${query.toString()}`, getAccessToken, { signal });
    },
    createChannel: (orgId, name) =>
      requestJson(`${org(orgId)}/channels`, getAccessToken, { method: 'POST', body: { name } }),
    listMessages: async (orgId, channelId, cursor, signal) => {
      const query = new URLSearchParams({ limit: String(PAGE_SIZE) });
      if (cursor && 'before' in cursor) query.set('before', String(cursor.before));
      if (cursor && 'after' in cursor) query.set('after', String(cursor.after));
      const page = await requestJson<MessagePage>(
        `${channel(orgId, channelId)}/messages?${query.toString()}`,
        getAccessToken,
        { signal },
      );
      if (!(cursor && 'after' in cursor)) return page;
      // `after` answers oldest first and its `next` points forward. Pages are kept newest first and a refetch rebuilds
      // the pages below the first one with `before={next}`, so `next` is made the cursor of the page below this one:
      // its oldest seq. An empty page has nothing newer, and nothing below it either.
      const newestFirst = page.messages.toReversed();
      return { messages: newestFirst, next: newestFirst.at(-1)?.seq ?? null };
    },
    sendMessage: (orgId, channelId, input) =>
      requestJson(`${channel(orgId, channelId)}/messages`, getAccessToken, { method: 'POST', body: input }),
    listMembers: (orgId, after, signal) =>
      requestJson(
        `${org(orgId)}/members?limit=100${after ? `&after=${encodeURIComponent(after)}` : ''}`,
        getAccessToken,
        { signal },
      ),
  };
}

/** The cell an organization lives in. */
export type Cell = { id: string; url: string };

/** Builds the API of one cell; the app provides it, tests provide fakes or the real one against MSW. */
export type MessagingApiFactory = (cell: Cell) => MessagingApi;

/** A token for one cell (audience `cell-{id}`). Never the control plane's token: a cell must not receive it. */
export type CellTokenSource = (cellId: string) => Promise<string>;

/** Null while messaging is off (the feature flag): then no cell is called. */
export const MessagingApiContext = createContext<MessagingApiFactory | null>(null);

export function useMessagingEnabled(): boolean {
  return useContext(MessagingApiContext) !== null;
}

export function useMessagingApiFactory(): MessagingApiFactory {
  const factory = useContext(MessagingApiContext);
  if (!factory) {
    throw new Error('Messaging is used outside MessagingApiContext.');
  }

  return factory;
}
