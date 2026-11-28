namespace Backroom.Modules.Messaging.Application.Messages;

/// <summary>One reaction of a message: who and when (<c>created_at</c>, as the database keeps it).</summary>
internal readonly record struct Reactor(Guid AccountId, DateTimeOffset At);

/// <summary>
/// One emoji of the stored summary (<c>messages.reaction_summary</c>): the number of people and the earliest
/// <see cref="ReactionSummary.MaxSample"/> of them with their times, ordered by (time, account id). The first sample time is the
/// emoji's first reaction time, which orders the summary; keeping the times is what lets a change update the summary without
/// reading all the message's rows.
/// </summary>
internal sealed record StoredReaction(string Emoji, int Count, IReadOnlyList<Reactor> Sample);
