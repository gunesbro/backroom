# Cell restore and failover

Background: [ADR 0011](../adr/0011-message-sync-read-state-and-storage-encryption.md), "Cell epoch". A restored database or a promoted replica can hand out sequence numbers that clients have already seen. The cell epoch tells clients that their cursors are void. The control plane holds it, so a restore cannot bring an old value back.

## How the fence works

- The control plane's registry holds the epoch (`cell_management.cells.epoch`, starting at 1). `Welcome` carries it to the cell's worker.
- The worker stores it in `control_plane_connector.cell_epoch` and marks it confirmed. The app role can read that table but not change it.
- The app role refuses every state-changing request (any method other than GET, HEAD, OPTIONS and TRACE, except the live-event hub) with `503 cell.epoch_unconfirmed` and `Retry-After: 5` while there is no confirmed row for its cell. Reads are never fenced. An app node takes up to 2 seconds to notice a change.
- An increment ends the cell's open link: right away on the control-plane instance that holds the stream, and at the next heartbeat (within about 30 seconds) on any other instance. The worker then clears the confirmation and reconnects. Writes are refused until the next `Welcome` brings the new epoch.

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
5. **Start the cell** (migrate, worker, app). Until the worker connects and receives the new epoch, the app answers writes with `503 cell.epoch_unconfirmed`; reads work.
6. **Check**: the worker logs "connected its link", `control_plane_connector.cell_epoch` shows the new epoch with `confirmed = true`, and a test write succeeds.

Steps 3 and 4 may be done in either order, but both must happen before step 5.

## Not yet

- Cursors do not carry the epoch yet (Phase 2 step 5). Until then the increment fences writes and makes the cell reconnect, but clients do not resync.
- Link handlers in the worker (messages from the control plane, integration events) are not fenced. Only requests to the app role are.
