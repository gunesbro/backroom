namespace Backroom.Modules.Messaging.Application.Messages;

/// <summary>What an edit or a delete needs to know about a message before it takes the conversation lock.</summary>
internal sealed record MessageHead(Guid Id, Guid ConversationId, Guid AuthorAccountId, bool Deleted);
