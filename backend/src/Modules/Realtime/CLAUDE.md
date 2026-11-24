# Realtime (cell)

Contract: [docs/contracts/phase-1.md](../../../../docs/contracts/phase-1.md) ("Identity and tokens", "Live events"). Technical module (no Domain project; no database tables).

## Responsibility

- SignalR hub `/hubs/realtime` in the `app` role, MessagePack protocol, Valkey backplane; `CloseOnAuthenticationExpiration` so connections end with their token.
- Groups per conversation (`cell:{cellId}:org:{orgId}:channel:{channelId}`) and per organization for content-free events. `SubscribeOrganization` / `SubscribeChannel` check membership and read access.
- `ILiveEventPublisher` (Realtime.Contracts) for modules, used after commit.
- Revoked sessions: `ISessionRevocation` (Realtime.Contracts) stores the session or account in Valkey until `keep_until`, closes matching connections on all nodes, and backs the authentication check that rejects revoked tokens on HTTP and hub connects.
- Consumes `organization.member-removed.v1` to remove the account's connections from that organization's groups.

## Owns

- No tables. Connection, group and revoked-session state live in SignalR and Valkey.

## Rules

- Live events never go through RabbitMQ and never wait for it.
- Payloads are versioned by event name (`MessagePosted`, later `MessagePosted.v2`), because clients may talk to cells on different versions.
- Message text is in live events but never in logs; the `access_token` query parameter is redacted.

## Status

- Implemented (Phase 1 step 6.1): only the contract and a placeholder. `Backroom.Modules.Realtime.Contracts` has `ILiveEventPublisher` (`PublishAsync` for `MessagePostedPayload` and `ChannelCreatedPayload`), the payload records exactly as the contract lists them, and `LiveEventNames`. `Backroom.Modules.Realtime.Infrastructure.AddRealtime()` registers a no-op publisher (`TryAdd`, so a real one registered first wins). The Valkey backplane, the SignalR hub, group subscriptions, MessagePack attributes on the payloads, `ISessionRevocation` and the `member-removed` consumer arrive in step 6.2 and later and replace the no-op.
- Modules call the publisher after their unit of work has committed and never let a failure reach the caller (Messaging's `AfterCommitLiveEvents`).
