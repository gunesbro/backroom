namespace Backroom.Modules.Messaging.Presentation;

/// <summary>Only the client id and the text: blocks are derived on the server and never read from the client.</summary>
public sealed record SendMessageRequest(Guid? ClientMessageId, string? Text);
