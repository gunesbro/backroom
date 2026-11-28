namespace Backroom.Modules.Messaging.Application.Messages;

/// <summary>
/// A message and the messages around it (docs/contracts/phase-2.md, "Jump to a message"). <paramref name="Messages"/> are newest
/// first and hold the anchor; <paramref name="Before"/> is the <c>before</c> cursor that continues to older messages and
/// <paramref name="After"/> the <c>after</c> cursor that continues to newer ones, each <see langword="null"/> when there is nothing
/// more on that side. <paramref name="Epoch"/> is the cell epoch both cursors belong to.
/// </summary>
internal sealed record MessageAround(IReadOnlyList<MessageEntry> Messages, MessageEntry Anchor, long? Before, long? After, string Epoch);
