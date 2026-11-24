namespace Backroom.Modules.Messaging.Application.Messages;

/// <summary>A message as the preview read sees it.</summary>
internal sealed record MessagePreviewSource(Guid Id, Guid ConversationId, string Text);
