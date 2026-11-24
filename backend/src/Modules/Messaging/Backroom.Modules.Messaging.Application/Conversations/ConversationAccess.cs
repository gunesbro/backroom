using Backroom.Modules.Messaging.Contracts;

namespace Backroom.Modules.Messaging.Application.Conversations;

/// <summary>What membership decisions need about one conversation, as seen by one account.</summary>
/// <param name="Kind">One of <see cref="ConversationKinds"/>.</param>
/// <param name="LastSeq">The conversation's last message sequence number, read without a lock.</param>
/// <param name="IsMember">Whether the account holds an open membership.</param>
internal sealed record ConversationAccess(string Kind, bool Archived, long LastSeq, bool IsMember)
{
    public bool IsPublicChannel => string.Equals(Kind, ConversationKinds.PublicChannel, StringComparison.Ordinal);

    /// <summary>
    /// Read access (docs/contracts/phase-2.md, flow 1): an open public channel, or an open conversation of another kind
    /// the account is a member of. Threads and archived conversations wait for their steps (refused until then).
    /// </summary>
    public bool CanRead => !Archived
        && !string.Equals(Kind, ConversationKinds.Thread, StringComparison.Ordinal)
        && (IsPublicChannel || IsMember);
}
