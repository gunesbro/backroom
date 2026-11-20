# 0001. Modular monolith deployed as cells

- Status: Accepted
- Date: 2026-10-25

## Context

The product is built by a single developer working with AI agents. It must start small, scale to 5,000+ concurrent users and later 100,000, support customers who want dedicated or self-hosted deployments (hybrid), and keep each organization's data in one region. Microservices would bring operational load that a one-person team cannot carry; a single shared monolith cannot isolate large customers or regions.

## Decision

- One code base and one deployable package, organized as a modular monolith. Each module owns its data in its own database schema and exposes only published contracts (interfaces, integration events) to other modules.
- Organization data is partitioned into cells. A cell is a full copy of the package with its own PostgreSQL, Valkey, RabbitMQ, object storage and LiveKit resources. All of one organization's data lives in exactly one cell.
- Cell types: multi-tenant cloud cell, dedicated cell (large or BYOK customers), hybrid cell on the customer's servers. All run the same package.
- The same binary runs in two roles: `app` (REST and SignalR endpoints, scaled horizontally with a Valkey backplane) and `worker` (queue consumers, scheduled jobs, email, calendar sync, recording processing).
- A thin control plane holds only cross-organization data (see ADR 0002).

## Consequences

- Module boundaries are enforced by project references at compile time and by ArchUnitNET architecture tests in CI. A module may reference only another module's `Contracts` project; everything else is `internal`.
- A failing cell does not affect other cells. A growing customer scales by moving to its own cell.
- Cross-module communication goes through contracts and events, so a module can later be extracted if ever needed, without being designed as a service from day one.
- Local development runs the whole system with .NET Aspire; end-to-end tests run a control plane with two cells.
