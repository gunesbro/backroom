using Backroom.ControlPlane.Contracts.V1;

namespace Backroom.Modules.CellManagement.Application.Link;

/// <summary>
/// A pending downstream message: its id, sequence, attempts so far, the cell's first answer if it already
/// answered (our handler failed; <see cref="AcknowledgedAnswer"/> or a rejection code), and its payload
/// (without envelope).
/// </summary>
internal sealed record OutboundMessage(Guid MessageId, long Sequence, int Attempts, string? CellAnswer, ConnectResponse Message)
{
    /// <summary>The stored answer for an <c>Ack</c>; rejection codes never contain a colon.</summary>
    public const string AcknowledgedAnswer = ":acknowledged";

    /// <summary>The rejection code of the cell's answer; <see langword="null"/> for an <c>Ack</c>.</summary>
    public string? RejectedCode => CellAnswer == AcknowledgedAnswer ? null : CellAnswer;
}
