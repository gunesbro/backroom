# Architecture decision records

Each record captures one significant decision: its context, the decision and its consequences. Records are immutable once accepted; a changed decision gets a new record that supersedes the old one.

| # | Decision | Status |
| --- | --- | --- |
| [0001](0001-modular-monolith-with-cells.md) | Modular monolith deployed as cells | Accepted |
| [0002](0002-thin-control-plane-with-keycloak.md) | Thin control plane with Keycloak for identity | Accepted |
| [0003](0003-cell-to-control-plane-outbound-grpc.md) | Cells open a single outbound gRPC stream to the control plane | Accepted |
| [0004](0004-data-residency.md) | Data residency: global identity, regional content | Accepted |
| [0005](0005-tenant-isolation-with-org-id-and-rls.md) | Tenant isolation with `org_id` and PostgreSQL RLS | Accepted |
| [0006](0006-transactional-outbox-and-thin-rabbitmq-layer.md) | Transactional outbox and a thin RabbitMQ layer | Accepted |
| [0007](0007-tauri-for-desktop.md) | Tauri for the desktop app | Accepted for Windows; Linux pending |
| [0008](0008-email-safety-outside-production.md) | Email never leaves non-production environments | Accepted |
| [0009](0009-keycloak-features.md) | Keycloak: account settings in our app, unused features off | Accepted |
| [0010](0010-maintenance-role-for-cross-organization-jobs.md) | A maintenance database role for cross-organization jobs | Proposed |

## Template

```markdown
# NNNN. Title

- Status: Proposed | Accepted | Superseded by NNNN
- Date: YYYY-MM-DD

## Context
## Decision
## Consequences
```
