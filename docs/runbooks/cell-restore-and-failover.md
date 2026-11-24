# Cell restore and failover

Background: [ADR 0011](../adr/0011-message-sync-read-state-and-storage-encryption.md), "Cell epoch". A restored database or a promoted replica can hand out sequence numbers that clients have already seen. The cell epoch tells clients that their cursors are void. The control plane holds it, so a restore cannot bring an old value back.

## How the fence works

- The control plane's registry holds the epoch (`cell_management.cells.epoch`, starting at 1). `Welcome` carries it to the cell's worker.
- The worker stores it in `control_plane_connector.cell_epoch` and marks it confirmed. The app role can read that table but not change it.
- The app role refuses every state-changing request (any method other than GET, HEAD, OPTIONS and TRACE, except the live-event hub) with `503 cell.epoch_unconfirmed` and `Retry-After: 5` while there is no confirmed row for its cell. Reads are never fenced. An app node takes up to 2 seconds to notice a change.
- An increment ends the cell's open link: right away on the control-plane instance that holds the stream, and at the next heartbeat (within about 30 seconds) on any other instance. The worker then clears the confirmation and reconnects. Writes are refused until the next `Welcome` brings the new epoch.
- The worker fences itself the same way: integration-event consumers neither handle nor acknowledge a delivery while the epoch is unconfirmed (the broker keeps it and it is offered again about every 5 seconds), and maintenance jobs skip their runs. Messages from the control plane are handled only after `Welcome`, so after the confirmation.

## Procedure

Follow these steps for every restore of a cell database (backup, point in time) and every promotion of a replica, whether planned or not.

1. **Stop the cell's app and worker nodes**, or keep them stopped. Do not let the old primary accept writes again (fence it at the network or stop it).
2. **Restore the database or promote the replica.**
3. **Clear the confirmation in the restored database** before any cell node starts, as the migrator role:

   ```sql
   UPDATE control_plane_connector.cell_epoch SET confirmed = false, updated_at = now();
   ```

   The restored copy still says "confirmed" with the old epoch. Without this step the app role would accept writes on it until the worker reconnects.

4. **Increment the epoch in the control plane**: `POST /api/v1/ops/cells/{id}/epoch` with an operations token (role `ops`, signed in with a one-time password). The answer carries the new epoch. The call is written to the operations audit log with your account and the cell id. Calling it twice is harmless; clients only resync once more.
5. **Purge the cell's RabbitMQ queues and republish recent events**, before the worker starts. Events still in the queues (and their retry queues) were published by the old primary, some from rows the restore discarded; applying them to the restored database would bring back state that no longer exists. So purge every queue of the cell's virtual host (`cell-{id}`), dead-letter queues included. Purging can also drop events the restored database did publish but nobody consumed yet, so mark the events of the last hour before the restore point unpublished in every module's outbox, as the migrator role:

   ```sql
   UPDATE <module>.outbox SET published_at = NULL WHERE published_at > <restore point> - interval '1 hour';
   ```

   The relay publishes them again once the worker starts. That is safe because every consumer records each event id in its inbox in the same transaction as its effect (ADR 0006): an event the restored database already consumed is skipped, one it did not is applied once. Draining the queues instead would be wrong: it applies the discarded events.

6. **Start the cell** (migrate, worker, app). Until the worker connects and receives the new epoch, the app answers writes with `503 cell.epoch_unconfirmed`; reads work.
7. **Check**: the worker logs "connected its link", `control_plane_connector.cell_epoch` shows the new epoch with `confirmed = true`, and a test write succeeds.

Steps 3, 4 and 5 may be done in any order, but all must happen before step 6.

## What a restore loses

The cell is the source of truth for its organizations (memberships, roles, channels, messages), so whatever was committed after the restore point is gone, and nothing in the system brings it back. This includes **member removals and deactivations**: a person removed after the restore point is a member again once the cell is up. The control plane cannot repair this, because it holds only a view of the cell's memberships, and today the cell does not even send `MembershipChanged` yet. Until a design for this exists (open question, raised for the owner: it needs a contract change, for example keeping removals in the control plane and sending them down before the confirmation), the procedure is:

- prefer promoting a replica or restoring to the latest possible point, so the window is as short as it can be;
- tell the owners of the cell's organizations the restore point, so they can check and repeat removals, role changes and other administrative changes made after it;
- invitations accepted after the restore point are lost in the cell, while the control plane shows them as used: the people concerned must be invited again;
- revoked sessions are not lost: they live in the control plane, and `FullSync` applies them again right after `Welcome`.

## Not yet

- Cursors do not carry the epoch yet (Phase 2 step 5). Until then the increment fences writes and makes the cell reconnect, but clients do not resync.
- Reconciling memberships after a restore (see "What a restore loses").
