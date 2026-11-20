using Google.Protobuf;

namespace Backroom.Modules.CellManagement.Contracts;

/// <summary>
/// Handles an upstream message of type <typeparamref name="T"/> from a cell (docs/contracts/phase-1.md,
/// "Delivery"). Called in the unit of work that records the message id, so a duplicate is never handled
/// twice; the cell gets <c>Ack</c> after the commit. Every organization id the message names has already
/// been checked to belong to the sending cell; other ids in it (an <c>invitation_id</c>, an account) are not,
/// so a handler always looks them up together with that organization (for example by
/// <c>(organization_id, invitation_id)</c>), never on their own. Throw <see cref="CellMessageRejectedException"/>
/// to refuse the message for good; any other exception makes the cell send it again later.
/// </summary>
public interface ICellMessageHandler<T>
    where T : IMessage
{
    Task HandleAsync(CellMessage<T> message, CancellationToken cancellationToken);
}
