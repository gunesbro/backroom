# 0010. A maintenance database role for cross-organization jobs

- Status: Proposed
- Date: 2026-11-20

## Context

Cell tables carry `org_id` and row-level security (ADR 0005); the application role sees only the organization of the current request. Some scheduled work has to visit every organization in one pass: marking invitations expired, deleting data past its retention (invitation email addresses, email delivery rows) and, later, closing polls, deleting meeting chat and similar privacy-driven cleanup. Running such a job once per organization does not scale (it is one query per organization, at 5,000 users and more).

Today the cell's invitation jobs (Organization module) run as `backroom_relay`, the outbox relay's role (ADR 0006), which already has BYPASSRLS. That mixes two jobs in one role: the relay's grants on outbox and inbox tables, and column grants on `organization.invitations` (`SELECT` of `id, org_id, status, expires_at, expired_at, revoked_at, email`; `UPDATE` of `status, expired_at, email`). Each new job would widen the relay's privileges further, and a fault in either part gets the other's access. The relay's connection pool is also sized for publishing, not for batch jobs.

The control plane is out of scope: its invitation jobs run as its application role, because its tables are not scoped by organization.

## Decision

- A fourth cell database role, `backroom_maintenance`, runs cross-organization maintenance jobs. It has `BYPASSRLS` and `NOINHERIT`, owns nothing, is not a superuser, cannot create roles or databases, holds no server privileges (`pg_read_server_files` and similar), and has `CONNECT` on the cell database and `USAGE` on module schemas.
- By default it has **no table privileges**. The migration of the module that owns a job grants the exact columns that job reads and changes (`GRANT SELECT (…)`, `GRANT UPDATE (…)`, `DELETE` only where the job deletes rows). It never gets content columns (message bodies, file names, email content) unless the job itself erases them, and then only `UPDATE` of those columns, never `SELECT`.
- Jobs implement one building-block interface, `IMaintenanceJob` (name `{module}.{job-name}`, interval from 10 s to 1 day, batch size up to 1,000). They run in the worker role under one runner that owns the role's small connection pool (`Max Pool Size` 2 by default). That pool is never registered in dependency injection, so no request handler can obtain a connection that bypasses RLS. An architecture test enforces this.
- Before first use the runner checks that its role is a dedicated BYPASSRLS role (one check in `DatabaseRoleGuard`, shared with the outbox relay; the relay's check becomes stricter too, because today it does not reject server privileges). If the check fails, the worker does not start.
- Every batch is bounded and never waits: `LIMIT`, `FOR UPDATE SKIP LOCKED`, a (usually partial) index on the filter, one short transaction per batch, and at most a fixed number of batches per run. Times come from `TimeProvider` as parameters, not from the database clock. A job returns counts. Logs and traces carry the job name, counts and ids only, never row values. Server error details are off on this pool.
- The runner adds jitter to each interval and a random start delay, so replicas do not run in step; `SKIP LOCKED` makes concurrent replicas safe.
- The relay keeps only its outbox and inbox privileges. A migration revokes the invitation grants from `backroom_relay` and grants them to `backroom_maintenance`, in the same change that moves the invitation jobs.
- Locally the AppHost generates and persists the role's password as a secret parameter, and the development bootstrap creates the role. Deployments provision it themselves, like the other roles.

## Consequences

- Each role has one purpose and the smallest privileges for it: a fault in a maintenance job cannot touch outboxes, and a relay fault cannot touch invitation data.
- Every new job needs a migration with explicit grants, so the job's data access is visible in review. Reviews check that a job reads no content and that its grants match its queries.
- One more secret and connection pool per cell (2 connections by default).
- The role bypasses RLS, so organization isolation for these jobs rests on their SQL and their grants, not on policies. This is why jobs only change state or delete data, never return rows to callers, and why the role's pool cannot be reached from request code.
- Code touching this role is a human-review area (permissions).

## Implementation

Planned in three PRs, each with its own review:

1. Building block: role constant, bootstrap, migrator schema grants, `DatabaseRoleGuard.IsDedicatedBypassRoleAsync` shared with the outbox relay, `IMaintenanceJob`, the runner and its options (validated at startup), the AppHost parameter, the architecture test, and integration tests for the role checks and the runner.
2. Organization: the invitation expiry and retention jobs become maintenance jobs, and a migration moves their grants from `backroom_relay` to `backroom_maintenance`.
3. Email: retention of stale delivery rows as a maintenance job.
