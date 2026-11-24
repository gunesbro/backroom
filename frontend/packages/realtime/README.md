# @backroom/realtime

A thin client over `@microsoft/signalr` (MessagePack, WebSockets only, no negotiate request) for one cell's hub at
`/hubs/realtime`. The library is loaded lazily on the first connect.

- `connected` is reported only after the groups were rejoined, so a catch-up that starts on `connected` cannot miss
  messages posted before the join.
- Reconnects never stop until `stop()` (or `SessionRevoked`): each delay is exponential with full jitter, at most
  30 s. A token is requested on every attempt, so an expired one is replaced.
- A group the hub refuses (`realtime.not_found`) is dropped from the replay; refusing an organization drops its
  channels too, so reconnects do not run into the hub's refusal limit.
- Live events are hints and can be dropped; the HTTP API is the source of truth (catch-up by `seq`).
