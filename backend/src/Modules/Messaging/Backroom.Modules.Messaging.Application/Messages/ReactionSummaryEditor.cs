namespace Backroom.Modules.Messaging.Application.Messages;

/// <summary>
/// Changes one emoji's entry of the stored reaction summary by one reaction, without reading the message's reaction rows or its other
/// entries: an add changes the entry's count and sample, a removal does too and asks for the sample to be read again (an indexed read of
/// three rows) only when the removed person was in it. The store then replaces that entry and puts the entries in their order (first
/// reaction time, then code point), so the result is the same as a full rebuild from all the rows: the sample is the three earliest
/// reactors by (time, account id as the database orders a uuid); an emoji with no reaction left has no entry.
/// </summary>
internal static class ReactionSummaryEditor
{
    /// <param name="entry">The emoji's entry before the change, <see langword="null"/> if no one had reacted with it.</param>
    public static StoredReaction Added(StoredReaction? entry, string emoji, Reactor reactor) =>
        entry is null
            ? new StoredReaction(emoji, 1, [reactor])
            : entry with { Count = entry.Count + 1, Sample = Earliest([.. entry.Sample, reactor]) };

    /// <summary>Whether removing the account's reaction needs the emoji's earliest reactors read again (it was one of them and others remain).</summary>
    public static bool NeedsRefill(StoredReaction? entry, Guid accountId) =>
        entry is { Count: > 1 } && entry.Sample.Any(reactor => reactor.AccountId == accountId);

    /// <param name="entry">The emoji's entry before the removal.</param>
    /// <param name="accountId">Who removed the reaction.</param>
    /// <param name="refill">The emoji's earliest reactors after the removal, when <see cref="NeedsRefill"/> said so.</param>
    /// <returns>The entry after the removal, <see langword="null"/> when no reaction with the emoji is left.</returns>
    public static StoredReaction? Removed(StoredReaction? entry, Guid accountId, IReadOnlyList<Reactor>? refill)
    {
        if (entry is null || entry.Count <= 1)
        {
            return null;
        }

        return entry with
        {
            Count = entry.Count - 1,
            Sample = entry.Sample.Any(reactor => reactor.AccountId == accountId)
                ? Earliest(refill ?? throw new InvalidOperationException("The sample must be read again."))
                : entry.Sample,
        };
    }

    private static IReadOnlyList<Reactor> Earliest(IEnumerable<Reactor> reactors) =>
        [.. reactors.OrderBy(reactor => reactor.At).ThenBy(reactor => reactor.AccountId.ToString("N"), StringComparer.Ordinal).Take(ReactionSummary.MaxSample)];
}
