using Google.Protobuf;

namespace Backroom.Modules.ControlPlaneConnector.Infrastructure.Link;

/// <summary>
/// Handles a downstream message of type <typeparamref name="T"/> by calling the owning module's contract
/// (docs/contracts/phase-1.md, ControlPlaneConnector CLAUDE.md). Called in the unit of work that records the
/// message id in the inbox, so a duplicate is never handled twice; for an organization's message the unit
/// of work is already in that organization. Throw <see cref="LinkMessageRejectedException"/> to refuse the
/// message for good, <see cref="LinkPrerequisiteMissingException"/> when the cell has not seen what it builds on
/// yet; any exception except the first makes the control plane send it again later.
/// </summary>
internal interface IDownstreamHandler<T>
    where T : IMessage
{
    Task HandleAsync(Guid messageId, T message, CancellationToken cancellationToken);
}
