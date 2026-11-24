# 0010. A maintenance database role for cross-organization jobs

- Status: Accepted
- Date: 2026-11-20

## Context

Cell tables carry `org_id` and row-level security (ADR 0005); the application role sees only the organization of the current request. Some scheduled work has to visit every organization in one pass: marking invitations expired, deleting data past its retention (invitation email addresses, email delivery rows) and, later, closing polls, deleting meeting chat and similar privacy-driven cleanup. Running such a job once per organization does not scale (it is one query per organization, at 5,000 users and more).

Today the cell's invitation jobs (Organization module) run as `backroom_relay`, the outbox relay's role (ADR 0006), which already has BYPASSRLS. That mixes two jobs in one role: the relay's grants on outbox and inbox tables, and column grants on `organization.invitations` (`SELECT` of `id, org_id, status, expires_at, expired_at, revoked_at, email`; `UPDATE` of `status, expired_at, email`). Each new job would widen the relay's privileges further, and a fault in either part gets the other's access. The relay's connection pool is also sized for publishing, not for batch jobs.

The control plane is out of scope: its invitation jobs run as its application role, because its tables are not scoped by organization.

## Decision

### The role

- A fourth cell database role, `backroom_maintenance`, runs cross-organization maintenance jobs. It has `BYPASSRLS` and `NOINHERIT`, owns nothing, is not a superuser, cannot create roles or databases, holds no server privileges (`pg_read_server_files` and similar), is not a member of any role and has no members, and has `CONNECT` on the cell database and `USAGE` on module schemas.
- The role has timeouts set on itself (`ALTER ROLE … SET`): `statement_timeout`, `lock_timeout` and `idle_in_transaction_session_timeout`. `SKIP LOCKED` skips locked rows but still waits for table locks, for example a migration's `ALTER TABLE`. With these timeouts a job gives up and retries in its next run instead of queueing behind the lock, and it never holds locks while idle.
- Before first use the runner checks that its role meets every point above except the timeouts: BYPASSRLS; not (a member of) a superuser; not (a member of) an owner of database objects; no server privileges; no role memberships in either direction. The check lives in `DatabaseRoleGuard` and is shared with the outbox relay. The relay's check becomes stricter too, because today it does not reject server privileges or role memberships. If the check fails, the worker does not start. Since PostgreSQL 16 a non-superuser administrator with `CREATEROLE` is made an ADMIN-only member (no `INHERIT`, no `SET`) of every role it creates; the check ignores such a membership, because the provisioning role is trusted and a role that passes the check cannot have `CREATEROLE` itself.

### Privileges

- By default the role has **no table privileges**. It is never named in `ALTER DEFAULT PRIVILEGES`, and it never gets `EXECUTE` on `SECURITY DEFINER` functions. The migration of the module that owns a job grants exactly the columns that job needs.
- PostgreSQL needs `SELECT` on every column a query references, including the columns in `WHERE`, so a job may filter only on state and time columns (ids, `org_id`, status, timestamps). It never gets `SELECT` on content columns (message bodies, file names, email addresses or email content). A job that erases content gets `UPDATE` of those columns only, and finds its rows through a non-content marker. For example, invitation retention sets `email = NULL, email_erased_at = $now` and filters on `email_erased_at IS NULL` instead of `email IS NOT NULL`.
- `FOR UPDATE SKIP LOCKED` needs `UPDATE` privilege on at least one column of the table (checked when the building block was implemented), so a job that only deletes also gets `UPDATE` on its least sensitive column, a state or time column, never content.
- `DELETE` is granted only to a job that deletes whole rows, together with `SELECT` on its filter columns. Foreign keys with `ON DELETE CASCADE` run as the table owner and so reach tables the role has no grants on. A migration that grants `DELETE` lists the cascades it can trigger, and every cascade stays inside the owning module's schema.

### Runner and jobs

- Jobs implement one building-block interface, `IMaintenanceJob`: a name `{module}.{job-name}`, an interval from 10 s to 1 day, and a batch size up to 1,000. They run in the worker role under one runner that owns the role's small connection pool (`Max Pool Size` 2 by default). This pool is never registered in dependency injection, so no request handler can obtain a connection that bypasses RLS. An architecture test enforces this for the maintenance pool. Today's keyed relay data sources (`UpstreamDelivery`, the invitation jobs) are not covered by it, and the invitation jobs' keyed data source goes away when they move.
- Every batch is bounded and never waits for rows: `LIMIT`, `FOR UPDATE SKIP LOCKED`, a (usually partial) index on the filter, one short transaction per batch, and at most a fixed number of batches per run. Times come from `TimeProvider` as parameters, not from the database clock. A job returns counts. Logs and traces carry the job name, counts and ids only, never row values. Server error details are off on this pool.
- The runner adds jitter to each interval and a random start delay, so replicas do not run in step. `SKIP LOCKED` makes concurrent replicas safe.
- Retention that falls behind is a privacy breach nobody sees, so it is measured. Each job reports, as OpenTelemetry metrics, the rows it handled per run, its backlog (due rows left after a run, bounded by a `LIMIT`ed count) and the age of its oldest due row. Runs that fail are counted as well. An alert fires when the oldest due row is older than the job's allowed lag, which by default is one day past due.

### Moving the invitation jobs

- The relay keeps only its outbox and inbox privileges. A migration revokes the invitation grants from `backroom_relay` and grants the maintenance role what the jobs need under the rules above. This happens in the same change that moves the invitation jobs.
- Locally the AppHost generates and persists the role's password as a secret parameter, and the development bootstrap creates the role. Deployments provision it themselves, like the other roles, including the role timeouts.

## Consequences

- Each role has one purpose and the smallest privileges for it: a fault in a maintenance job cannot touch outboxes, and a relay fault cannot touch invitation data.
- Every new job needs a migration with explicit grants, so the job's data access is visible in review. Reviews check that the job reads no content, that its grants match its queries, and that any cascades stay inside the module.
- Jobs that erase content need a marker column so they can find their rows without reading the content.
- One more secret and connection pool per cell (2 connections by default).
- The role bypasses RLS, so organization isolation for these jobs rests on their SQL and their grants, not on policies. This is why jobs only change state or delete data and never return rows to callers, and why the role's pool cannot be reached from request code.
- Code touching this role is a human-review area (permissions).
- Later addition (Phase 2 step 4, ADR 0011 "Cell epoch"): the relay role, which only the worker's link uses, also writes the cell's epoch confirmation (`control_plane_connector.cell_epoch`: `SELECT`, `INSERT`, and `UPDATE` of `epoch, confirmed, updated_at` only, never `cell_id`). The app role may only read that table, so request code cannot confirm its own epoch. The table holds no organization data, so the relay's bypass of RLS gives it nothing more there.

## Implementation

Planned in three PRs, each with its own review:

1. Building block:
   - the role constant, the fourth bootstrap password, the `CONNECT` grant and the role timeouts;
   - the migrator's schema `USAGE` grant and its migrations-history revoke;
   - `DatabaseRoleGuard.IsDedicatedBypassRoleAsync`, shared with the outbox relay and including the role-membership check;
   - `IMaintenanceJob`, the runner and its options (validated at startup), and its metrics;
   - the AppHost parameter;
   - the architecture test;
   - integration tests for the role checks, the timeouts and the runner.
2. Organization: the invitation expiry and retention jobs become maintenance jobs. A migration adds `email_erased_at`, moves the grants from `backroom_relay` to `backroom_maintenance` and drops `SELECT` on `email`.
3. Email: closing stale `pending` delivery rows as `unknown` and deleting deliveries after 90 days, as maintenance jobs.
