using Backroom.Modules.Messaging.Contracts;

namespace Backroom.Modules.Messaging.Application.Conversations;

/// <summary>A conversation as one account sees it, for the conversation's own routes and for membership changes.</summary>
/// <param name="Kind">One of <see cref="ConversationKinds"/>.</param>
/// <param name="OwnerAccountId">The owner of a private channel.</param>
/// <param name="Role">The account's role (<see cref="MemberRoles"/>) while it holds an open membership, else <see langword="null"/>.</param>
internal sealed record ConversationDetails(
    Guid Id,
    string Kind,
    string? Name,
    string? Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ArchivedAt,
    long ChangeSeq,
    int MemberCount,
    Guid? OwnerAccountId,
    string? Role)
{
    public bool IsPublicChannel => string.Equals(Kind, ConversationKinds.PublicChannel, StringComparison.Ordinal);

    public bool IsPrivateChannel => string.Equals(Kind, ConversationKinds.PrivateChannel, StringComparison.Ordinal);

    public bool IsMember => Role is not null;

    public bool IsOwner => string.Equals(Role, MemberRoles.Owner, StringComparison.Ordinal);

    public bool Archived => ArchivedAt is not null;

    /// <summary>
    /// Whether the account may know the conversation exists: a public channel, or a conversation it is a member of.
    /// Everything else answers as if there were no such conversation, whatever the account's role in the organization.
    /// </summary>
    public bool IsVisible => IsPublicChannel || IsMember;
}
