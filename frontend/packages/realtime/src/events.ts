// Live events of the cell's hub (docs/contracts/phase-1.md and phase-2.md, "Live events"). Payloads are MessagePack
// maps with camelCase keys. Events are hints: the HTTP API stays the source of truth, and clients catch up by `seq`.

export type LiveMessage = {
  id: string;
  seq: number;
  clientMessageId: string;
  authorAccountId: string;
  text: string;
  createdAt: string;
};

export type MessagePosted = { orgId: string; channelId: string; message: LiveMessage };
export type ChannelCreated = { orgId: string; channel: { id: string; name: string; createdAt: string } };
export type SessionRevoked = { reason: string };
/** To the account's own group (docs/contracts/phase-2.md); `removed` also stops delivery of that conversation's events. */
export type MembershipChanged = { orgId: string; conversationId: string; change: 'added' | 'removed' };

/** Automatic presence (docs/contracts/phase-2.md, flow 9); never platform or device. */
export type PresenceState = 'online' | 'idle' | 'offline';

/** A custom status: an emoji and/or a text of at most 100 characters, with an optional expiry (ISO 8601). */
export type CustomStatus = { emoji: string | null; text: string | null; expiresAt: string | null };

/** Sent to the watchers of a person (`presence:{accountId}` groups), at most one per person per 5 s. */
export type PresenceChanged = {
  orgId: string;
  accountId: string;
  state: PresenceState;
  /** ISO 8601; clients treat a past time as off. */
  dndUntil: string | null;
  customStatus: CustomStatus | null;
};

export type RealtimeEvents = {
  MessagePosted: MessagePosted;
  ChannelCreated: ChannelCreated;
  SessionRevoked: SessionRevoked;
  PresenceChanged: PresenceChanged;
  MembershipChanged: MembershipChanged;
};

export type RealtimeEventName = keyof RealtimeEvents;

export const EVENT_NAMES: readonly RealtimeEventName[] = [
  'MessagePosted',
  'ChannelCreated',
  'SessionRevoked',
  'PresenceChanged',
  'MembershipChanged',
];

/** What a client reports about its person's input; the server takes one report per 10 s per connection. */
export type ActivityState = 'active' | 'idle';

/** The most people one connection may watch (`WatchPresence`). */
export const MAX_WATCHED_ACCOUNTS = 200;

/** `connecting`: first attempt; `reconnecting`: after a loss; `stopped`: closed on purpose or the session ended. */
export type ConnectionStatus = 'connecting' | 'connected' | 'reconnecting' | 'stopped';
