# 0005. Tenant isolation with `org_id` and PostgreSQL RLS

- Status: Accepted
- Date: 2026-10-25

## Context

Multi-tenant cells hold many organizations in the same databases. A single missing filter in a query must not expose another organization's data. Privacy is the first product principle.

## Decision

- Every table that holds organization data has an `org_id` column, and PostgreSQL row-level security is enabled on it.
- The organization context of a request is derived from authentication: the route names the organization, and nothing runs in it for the client until the authenticated account passes the membership check. Row-level security needs the context for that check, so it is entered first, and a failed check rolls back.
- Each module owns its own schema (`messaging`, `office`, …); no module touches another module's tables.
- Search, notifications and (later) AI retrieval go through the same permission check as regular reads.
- Files are downloaded through short-lived signed URLs; access is checked again on every request.
- Primary keys are UUIDv7: time-ordered and generated without coordination.

## Consequences

- Integration tests run against real PostgreSQL (Testcontainers) and include a dedicated privacy test suite: at least one test per privacy rule, for example that an admin cannot read a private channel.
- Hot queries written in SQL by hand are subject to the same RLS policies.

## Implementation (confirmed by the Phase 1 contracts)

See [docs/contracts/phase-1.md](../contracts/phase-1.md), "Cross-cutting rules".

- Every request and message handler runs in one transaction (the unit of work); the organization context is set transaction-locally with `set_config('app.org_id', ..., true)`, so it is safe with pooled connections.
- Policy `org_isolation` on every organization table, for all commands, with `FORCE ROW LEVEL SECURITY` so it applies to the table owner too: `org_id = NULLIF(current_setting('app.org_id', true), '')::uuid` in both `USING` and `WITH CHECK`. `NULLIF` is needed because, after a connection has used the transaction-local setting once, PostgreSQL reports it as an empty string in later transactions; without it the uuid cast fails instead of matching no row.
- No other policy may exist on an organization table: permissive policies are OR-ed, so an extra one could expose other organizations' rows. `RowLevelSecurityAudit` checks the live schema for this in module integration tests.
- The application's database role cannot bypass RLS (no SUPERUSER, no BYPASSRLS, not a member of such a role, not a table owner); services refuse to start a unit of work otherwise. Migrations run under a separate role that owns the tables.
- The outbox relay and inbox writers use a dedicated role limited to outbox and inbox tables.
- Only the unit of work sets `app.org_id`; a Semgrep rule rejects raw SQL elsewhere that changes it, switches roles or turns `row_security` off. A database error aborts the unit of work.
- Rules for module schemas, because some checks ignore RLS: the application role gets only `SELECT, INSERT, UPDATE, DELETE` (never `TRUNCATE`, which RLS does not cover); foreign keys between organization tables are composite `(org_id, id)` and unique constraints include `org_id`, so constraint errors cannot reveal another organization's rows; views use `security_invoker = true`.
- Migrations: each host runs its modules' migrations in a separate run before it starts (`Backroom.Cell.Host` with `Cell__Role=migrate`, `Backroom.ControlPlane.Host migrate`), as `backroom_migrator`, which must have neither SUPERUSER nor BYPASSRLS, cannot create roles and cannot use the server's files or programs. For each module it creates the schema, lets the application, relay and maintenance roles use it, gives the application role data access (no DDL, no `TRUNCATE`) to new tables, applies the EF Core migrations, removes every service role's access to the migration history (so injected SQL cannot mark a migration as applied), and runs `RowLevelSecurityAudit` on organization-scoped modules (every cell module). The audit also rejects materialized views, foreign tables and views without `security_invoker`. A violation fails the run. Functions the migrator creates are not executable by PUBLIC. **Deployments must start the services only after the migrate run succeeded** (the AppHost waits for its completion). In development the run first creates the four roles (migrator, application, relay, maintenance; the maintenance role is described in ADR 0010 and also gets its role-level timeouts) with generated passwords through the administrator connection (`DatabaseBootstrapper`): passwords are passed as bind parameters, the session's statements and parameters are not logged, errors are re-raised without the statement, concurrent runs take turns, and PUBLIC loses `CONNECT` and `TEMP` on the database. Elsewhere the deployment provisions the roles the same way, and configuring the bootstrap stops the run.

Code: `Backroom.BuildingBlocks.Infrastructure.Persistence` (`DatabaseSession`, `AddModuleDbContext`, `EnableOrganizationRowLevelSecurity`, `RowLevelSecurityAudit`) and the `WithUnitOfWork()` endpoint filter. Tests: `Backroom.BuildingBlocks.IntegrationTests` (category `Privacy`).
