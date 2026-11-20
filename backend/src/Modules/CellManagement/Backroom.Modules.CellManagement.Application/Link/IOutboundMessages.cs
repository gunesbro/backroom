namespace Backroom.Modules.CellManagement.Application.Link;

/// <summary>
/// The downstream link messages (<c>cell_management.outbound_messages</c>). Leasing runs on its own; locking, settling and retrying need the scope's active unit of work. Every method filters by
/// the cell.
/// </summary>
internal interface IOutboundMessages
{
    /// <summary>
    /// Leases up to <paramref name="limit"/> of the cell's due messages for <paramref name="streamId"/> until
    /// <paramref name="leaseUntil"/> and returns them in sequence order. A message is due unless it waits for a
    /// retry or is leased by this or a newer stream; taking over a lease (a send without an answer) counts as an attempt.
    /// </summary>
    Task<IReadOnlyList<OutboundMessage>> LeaseDueAsync(
        string cellId, Guid streamId, DateTimeOffset now, DateTimeOffset leaseUntil, int limit, CancellationToken cancellationToken);

    /// <summary>Locks the cell's pending message <paramref name="messageId"/>; <see langword="null"/> if it is not pending or belongs to another cell.</summary>
    Task<OutboundMessage?> LockPendingAsync(string cellId, Guid messageId, CancellationToken cancellationToken);

    /// <summary>Marks the message acknowledged (<paramref name="rejectedCode"/> null) or rejected, and deletes its payload.</summary>
    Task SettleAsync(string cellId, Guid messageId, string? rejectedCode, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Counts an attempt and delays the next one (2 s doubling to 5 min from <paramref name="now"/>);
    /// <paramref name="cellAnswer"/>, if given and none is stored yet, keeps the cell's answer. Returns the
    /// attempts so far and the stored answer, or <see langword="null"/> if the message is not pending.
    /// </summary>
    Task<(int Attempts, string? CellAnswer)?> ScheduleRetryAsync(
        string cellId, Guid messageId, string? cellAnswer, DateTimeOffset now, CancellationToken cancellationToken);
}
