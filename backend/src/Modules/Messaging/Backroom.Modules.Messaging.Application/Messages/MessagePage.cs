namespace Backroom.Modules.Messaging.Application.Messages;

/// <summary>
/// One page of messages. With <c>after</c> the messages are oldest first and <paramref name="Next"/> is the <c>after</c>
/// of the following page; with <c>changed_after</c> they are in the order of their latest change and
/// <paramref name="Next"/> is the following <c>changed_after</c>; otherwise they are newest first and
/// <paramref name="Next"/> is the <c>before</c> of the following (older) page. <see langword="null"/> when there is no
/// further page. <paramref name="Epoch"/> is the cell epoch all of these numbers belong to.
/// </summary>
internal sealed record MessagePage(IReadOnlyList<MessageEntry> Messages, long? Next, string Epoch);
