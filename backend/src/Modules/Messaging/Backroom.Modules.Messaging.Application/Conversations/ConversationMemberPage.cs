namespace Backroom.Modules.Messaging.Application.Conversations;

/// <summary>One page of members by account id; <paramref name="Next"/> is the <c>after</c> of the following page, if there is one.</summary>
internal sealed record ConversationMemberPage(IReadOnlyList<ConversationMember> Items, Guid? Next);
