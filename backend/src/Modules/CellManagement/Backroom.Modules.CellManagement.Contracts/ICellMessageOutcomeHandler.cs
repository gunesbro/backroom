using Google.Protobuf;

namespace Backroom.Modules.CellManagement.Contracts;

/// <summary>
/// Learns how a cell answered a downstream message of type <typeparamref name="T"/>. Called in the unit of
/// work that settles the message, so the handler's changes and the settlement commit together; a handler
/// that throws leaves the message pending, and it is sent again later.
/// </summary>
public interface ICellMessageOutcomeHandler<T>
    where T : IMessage
{
    /// <summary>The cell committed its handling of the message.</summary>
    Task OnAcknowledgedAsync(CellMessage<T> message, CancellationToken cancellationToken);

    /// <summary>The cell refused the message for good with <paramref name="code"/>.</summary>
    Task OnRejectedAsync(CellMessage<T> message, string code, CancellationToken cancellationToken);
}
