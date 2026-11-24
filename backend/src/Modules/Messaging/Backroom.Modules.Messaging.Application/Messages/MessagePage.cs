namespace Backroom.Modules.Messaging.Application.Messages;

/// <summary>
/// One page of messages. With <c>after</c> the messages are oldest first and <paramref name="Next"/> is the <c>after</c>
/// of the following page; otherwise they are newest first and <paramref name="Next"/> is the <c>before</c> of the
/// following (older) page. <see langword="null"/> when there is no further page.
/// </summary>
internal sealed record MessagePage(IReadOnlyList<MessageEntry> Messages, Guid? Next);
