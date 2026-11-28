namespace Backroom.Modules.Messaging.Application.Messages;

/// <summary>The message a reply would quote, as the send reads it once: its conversation and, unless it is deleted, author and text.</summary>
internal sealed record QuoteSource(Guid ConversationId, Guid AuthorAccountId, bool Deleted, string? Text);
