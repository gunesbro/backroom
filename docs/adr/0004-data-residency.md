# 0004. Data residency: global identity, regional content

- Status: Accepted
- Date: 2026-10-25

## Context

Customers in different countries need their content kept in a chosen region (for example Türkiye or Europe), while a person should still use one account across organizations in different regions. Regional control planes would split accounts and multiply operations.

## Decision

- Identity is global, content is regional. There is one control plane; no regional control planes.
- An organization chooses its region at creation; its cell is in that region. Region is a field of the cell record. Non-default regions are available on the Business and Enterprise plans.
- Stays in the region: messages, files, meetings (including media traffic) and recordings, search index, calendar, status history, audit logs, backups, and the cell's default external services (object storage, email, LiveKit, observability, AI).
- Stays in the control plane: account (name, email, credentials), sessions and device information, membership directory, invitations, license and usage summary.
- Moving an organization between cells (later) also covers changing region.

## Consequences

- Cross-border transfer rules (KVKK, GDPR) apply to the account data in the control plane; legal review happens before deployment.
- Customers that require that no data leaves their premises are served by a fully on-premises installation (not in the first release); the architecture must not block it, so no component assumes a single cloud provider and the client reads the control plane address from configuration.
