# 0006. Transactional outbox and a thin RabbitMQ layer

- Status: Accepted
- Date: 2026-10-25

## Context

Modules communicate through integration events, and state changes must not be lost or published without being committed. MassTransit v9 is paid, and MediatR v13+ has a commercial license; the project avoids libraries that moved to commercial terms. Real-time events (new message, typing, presence) must reach clients immediately and cannot wait for a queue.

## Decision

- A state-changing operation writes its integration event to an outbox table in the same database transaction.
- A thin layer of our own on top of the official RabbitMQ .NET client provides: a single event interface; outbox-to-RabbitMQ relay with publisher confirms; on the consumer side acknowledgements, retries with increasing back-off, a dead-letter queue and idempotency.
- The OpenTelemetry trace context travels with every message, so a request can be followed from the app role through the queue to the worker.
- Scheduled jobs (scheduled messages, reminders, meeting reminders) are stored in PostgreSQL.
- Live events are published through Valkey as soon as the transaction completes; they do not go through RabbitMQ.

## Consequences

- At-least-once delivery: consumers must be idempotent.
- The layer is small and under our control, but we own its tests: integration tests run against real RabbitMQ with Testcontainers, including redelivery and dead-letter paths.

## Implementation

Code: `Backroom.BuildingBlocks.Infrastructure.Messaging`; tests: `Backroom.BuildingBlocks.IntegrationTests` (real PostgreSQL and RabbitMQ).

- Events implement `IIntegrationEvent` (BuildingBlocks.Domain) with a type name `{module}.{event-name}.v{n}`; the module part is the publishing module's schema and selects the topic exchange `{module}.events`, and the type is the routing key. Payloads are JSON with snake_case names.
- `IOutbox.AddAsync` inserts the event into `{module}.outbox` in the current unit of work, with the W3C trace context of the current span. The outbox is organization data with the usual RLS policy; the application role may only insert into it.
- The outbox relay runs in the worker role as the `backroom_relay` database role: BYPASSRLS (it handles every organization) but privileges only on outbox tables (`SELECT`, `DELETE`, and `UPDATE` of `published_at` and `failed_at` only), granted by the migration that creates each one. It refuses to run without BYPASSRLS, as a superuser, as (a member of) an owner of database objects, with server privileges (`CREATEROLE`, `pg_read_server_files` and similar) or with role memberships in either direction (the check is shared with the maintenance role, ADR 0010). Events are limited to 64 KiB.
- Each round locks up to `BatchSize` unpublished rows (`FOR UPDATE SKIP LOCKED`, partial index on unpublished rows), publishes them as persistent messages with publisher confirms, and marks them published in the same transaction. Failures leave rows unpublished; a failing schema is retried with exponential back-off (up to 30 s) without delaying the others, and a batch waits at most 30 s for confirms. A row that can never be published (invalid type, another module's event type, oversized payload) gets `failed_at`, is skipped and logged; clearing `failed_at` retries it. Delivery is at least once and unordered; events that must be applied in order carry a version.
- Messages carry `message_id` = event id, `type` = event type, and headers `org_id`, `traceparent`, `tracestate`. Each `{module}.events` exchange has an alternate exchange: events no queue is bound for go to the quorum queue `{module}.events.unrouted` (at most 100,000 messages, 7 days) instead of being dropped. Consumers declare their queues and bindings at startup, before the worker's relay starts publishing; the unrouted queue is the safety net, not the plan.
- Published rows are deleted after `PublishedRetention` (default one day) in bounded batches.
- Tracing: one producer span per message, parented on the span that wrote the event; the relay's polling creates no spans (Npgsql commands are traced only inside a relay span, which starts when a batch has rows).
- Consumers: a module registers `AddIntegrationEventHandler<TEvent, THandler>(schema)`; the worker's `IntegrationEventConsumerHost` declares, before it starts consuming, the quorum queue `{consumer}.{event-type}` bound to the publisher's exchange (delivery limit 20, at-least-once dead-lettering), its quorum dead-letter queue `….dlq` (at most 100,000 messages and 14 days, because payloads may hold personal data the contract allows), and one quorum retry queue per configured delay (`….retry.{ms}ms`, TTL then back to the main queue at least once). Register consumers before the relay so queues exist before events are published; the relay cleans up the inbox of every module that registers a handler.
- Each delivery is checked (message id, `org_id` header and type must match the payload; otherwise it goes straight to the dead-letter queue), then handled in a unit of work in the event's organization. The handler's module records `(event_id, handler)` in its `{module}.inbox` table in the same transaction, so redeliveries are skipped; the delivery is acknowledged after the commit.
- A failing handler rolls back; the delivery is republished (with publisher confirms) to the next retry queue (default delays 5 s, 30 s, 2 min, 10 min) with an `x-backroom-attempt` header, and after the last retry to the dead-letter queue with `x-backroom-failure` (the exception type, plus PostgreSQL's error code, constraint and table when the database raised it; never the message) and `x-backroom-failed-at`. Transient infrastructure failures (database unreachable, deadlock, timeout) do not use up retries: the delivery is requeued after a pause that doubles up to 30 s. If republishing or acknowledging fails, the delivery is requeued after such a pause, and after 5 failures rejected to the dead-letter queue (a requeue does not count towards the broker's delivery limit). Exception messages may contain personal data, so failures are logged (and added to the span) with the exception types and stack traces of the chain but without messages; the full exception is logged at Debug only. To investigate a dead letter: its `traceparent` header leads to the trace, its spans (including SQL text without parameter values) and the logged stack trace; its body is the event, which can be replayed after the fix. A delivery that keeps crashing the consumer hits the quorum queue's delivery limit and is dead-lettered too. Each queue handles one delivery at a time (prefetch 16).
- Inbox rows are deleted by the relay after `InboxRetention` (default 14 days); a dead-lettered event replayed after that would be handled again.
