namespace Backroom.Modules.Messaging.Application.Messages;

/// <summary>
/// One emoji of a message's reaction summary (docs/contracts/phase-2.md, flow 2 "Reactions"): how many people reacted with it,
/// whether the reader is one of them and up to <see cref="MaxSample"/> of them, the earliest first. The stored summary
/// (<c>messages.reaction_summary</c>) holds <c>emoji</c>, <c>count</c> and <c>sample</c> only; <paramref name="ReactedByMe"/> is the
/// reader's own and is filled when a message is read.
/// </summary>
internal sealed record ReactionSummary(string Emoji, int Count, bool ReactedByMe, IReadOnlyList<Guid> Sample)
{
    public const int MaxSample = 3;
}
