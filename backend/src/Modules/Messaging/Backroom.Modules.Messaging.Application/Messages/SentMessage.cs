namespace Backroom.Modules.Messaging.Application.Messages;

/// <summary>
/// <paramref name="Created"/> is <see langword="false"/> when the client's id had been stored before and the existing
/// message is returned. <paramref name="Epoch"/> is the cell epoch the message's numbers belong to.
/// </summary>
internal sealed record SentMessage(MessageEntry Message, bool Created, string Epoch);
