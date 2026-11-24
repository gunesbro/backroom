namespace Backroom.Modules.Messaging.Application.Conversations;

/// <summary>One page of conversations by id; <paramref name="Next"/> is the <c>after</c> of the following page, if there is one.</summary>
internal sealed record ConversationPage(IReadOnlyList<ConversationEntry> Items, Guid? Next);
