namespace Backroom.Modules.Messaging.Application.Conversations;

/// <summary>One page of conversations by <c>(last message time DESC, id DESC)</c>; <paramref name="Next"/> is the opaque <c>after</c> of the following page, if there is one.</summary>
internal sealed record ConversationPage(IReadOnlyList<ConversationEntry> Items, string? Next);
