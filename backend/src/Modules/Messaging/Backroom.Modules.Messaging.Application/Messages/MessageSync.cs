namespace Backroom.Modules.Messaging.Application.Messages;

/// <summary>
/// The flag <c>Features:MessageSync</c> (docs/contracts/phase-2.md, "Sequence numbers"), which guards the two parts of
/// the cell epoch that would break a client that does not know it yet. On: a read with <c>before</c> or <c>after</c>
/// must name the epoch, and a send announces <c>MessagePosted.v2</c> instead of <c>MessagePosted</c>. Off (until the
/// clients send the epoch and listen to the new event): those two stay as in Phase 1. Nothing else depends on it.
/// </summary>
internal sealed record MessageSync(bool Enabled)
{
    public const string Flag = "Features:MessageSync";
}
