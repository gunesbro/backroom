using Backroom.ControlPlane.Contracts.V1;

namespace Backroom.Modules.CellManagement.Contracts;

/// <summary>
/// Sends durable messages to a cell over the link (docs/contracts/phase-1.md, "Delivery"). A message is
/// written in the caller's unit of work, so it is sent if and only if the caller's change commits; it is
/// delivered at least once, in order per cell, until the cell answers <c>Ack</c> or a non-retryable
/// <c>Nack</c>. Its payload is deleted then.
/// </summary>
public interface ICellLinkOutbox
{
    /// <summary>The largest serialized message; link messages carry ids and a few names.</summary>
    public const int MaxPayloadBytes = 64 * 1024;

    /// <summary>
    /// Enqueues <paramref name="message"/> (a downstream payload such as <c>OrganizationProvisioned</c>,
    /// without an envelope) for <paramref name="cellId"/>; returns its message id. Needs an active unit of work.
    /// </summary>
    Task<Guid> EnqueueAsync(string cellId, ConnectResponse message, CancellationToken cancellationToken);
}
