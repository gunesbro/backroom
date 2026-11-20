# 0003. Cells open a single outbound gRPC stream to the control plane

- Status: Accepted
- Date: 2026-10-25

## Context

Cells need control-plane events (logout, invitation acceptance, profile changes, account deletion, license changes, signing key rotation, release announcements) and the control plane needs cell data (heartbeat, membership changes, organization name and logo, usage summary). Hybrid cells run inside customer networks, where inbound connections are usually not allowed and corporate gateways may not pass HTTP/2. Hybrid cells may run up to two major versions behind.

## Decision

- Every cell, hybrid ones included, opens and keeps open one outbound gRPC bidirectional stream to the control plane on port 443. The control plane never connects to a cell. The approach mirrors the xDS protocol between control and data planes in Envoy and Istio: one stream, every message acknowledged or rejected.
- Messages are defined in Protobuf. Fields are only added with new numbers; existing fields are never removed or renumbered. `buf breaking` enforces this on every PR.
- Fallback: when HTTP/2 is blocked, the same contract runs over gRPC-Web on HTTP/1.1 (server streaming downstream, unary calls upstream).
- Authentication: at installation the cell generates a key pair; the private key never leaves the cell. On each connection the cell signs a request to obtain a short-lived credential from Keycloak. A cell's access can be revoked in one step.
- Delivery: every message has a sequence number. Unacknowledged messages wait in a per-cell queue in the control plane and are resent from where they left off after reconnection.
- Periodic full sync (proposed: every 15 minutes) lets a cell request its entire expected state and correct drift.
- Liveness uses HTTP/2 PING; reconnects use exponential backoff with jitter.
- User access tokens are short-lived (proposed: 5 minutes), bounding how late a logout can take effect during a disconnection.

## Consequences

- No customer network needs inbound rules; the protocol works through corporate proxies.
- Messages, files, meetings, calendars, status history and audit logs never cross this link; the protocol has no message types for them. Customers may log the traffic on their side.
- User clients keep using SignalR for real-time traffic; this link is for cell–control-plane traffic only.
