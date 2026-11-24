// Live events of the cell's hub (docs/contracts/phase-1.md, "Live events"). Payloads are MessagePack maps with
// camelCase keys. Events are hints: the HTTP API stays the source of truth, and clients catch up by `seq`.

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

export type RealtimeEvents = {
  MessagePosted: MessagePosted;
  ChannelCreated: ChannelCreated;
  SessionRevoked: SessionRevoked;
  MembershipChanged: MembershipChanged;
};

export type RealtimeEventName = keyof RealtimeEvents;

export const EVENT_NAMES: readonly RealtimeEventName[] = [
  'MessagePosted',
  'ChannelCreated',
  'SessionRevoked',
  'MembershipChanged',
];

/** `connecting`: first attempt; `reconnecting`: after a loss; `stopped`: closed on purpose or the session ended. */
export type ConnectionStatus = 'connecting' | 'connected' | 'reconnecting' | 'stopped';
