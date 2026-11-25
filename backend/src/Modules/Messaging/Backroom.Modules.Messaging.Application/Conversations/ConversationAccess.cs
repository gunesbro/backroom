using Backroom.Modules.Messaging.Contracts;

namespace Backroom.Modules.Messaging.Application.Conversations;

/// <summary>What membership decisions need about one conversation, as seen by one account.</summary>
/// <param name="Kind">One of <see cref="ConversationKinds"/>.</param>
/// <param name="ChangeSeq">The conversation's change counter (the number of its latest change), read without a lock.</param>
/// <param name="IsMember">Whether the account holds an open membership.</param>
internal sealed record ConversationAccess(string Kind, bool Archived, long ChangeSeq, bool IsMember)
{
    public bool IsPublicChannel => string.Equals(Kind, ConversationKinds.PublicChannel, StringComparison.Ordinal);

    /// <summary>
    /// Read access (docs/contracts/phase-2.md, flow 1): a public channel, or a conversation of another kind the account
    /// is a member of. An archived conversation stays readable (and read-only, see <see cref="CanPost"/>). Threads wait
    /// for their step (refused until then).
    /// </summary>
    public bool CanRead => !string.Equals(Kind, ConversationKinds.Thread, StringComparison.Ordinal)
        && (IsPublicChannel || IsMember);

    /// <summary>
    /// The right to post ("HTTP APIs": member; channels: joined, not archived): read access, an open membership and a
    /// conversation that is not archived. Every write of content asks this, never <see cref="CanRead"/>.
    /// </summary>
    public bool CanPost => CanRead && IsMember && !Archived;
}
