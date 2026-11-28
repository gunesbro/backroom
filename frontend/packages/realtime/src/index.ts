// Multi-cell SignalR client (MessagePack, WebSockets only). The SignalR library itself is loaded lazily on connect.
export {
  backoffWithJitter,
  createRealtimeClient,
  type RealtimeClient,
  type RealtimeListener,
  type RealtimeOptions,
} from './client';
export { MAX_WATCHED_ACCOUNTS } from './events';
export type {
  ActivityState,
  ChannelCreated,
  ConnectionStatus,
  ConversationUpdated,
  CustomStatus,
  LiveMessage,
  MembershipChanged,
  MessageDeleted,
  MessageEdited,
  MessagePosted,
  PresenceChanged,
  PresenceState,
  RealtimeEventName,
  RealtimeEvents,
  SessionRevoked,
} from './events';
export type { Hub, HubFactory } from './hub';
