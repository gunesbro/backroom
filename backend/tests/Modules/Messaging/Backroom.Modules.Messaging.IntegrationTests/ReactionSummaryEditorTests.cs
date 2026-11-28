using System.Globalization;
using AwesomeAssertions;
using Backroom.Modules.Messaging.Application.Messages;

#pragma warning disable CA5394 // A seeded Random makes the test sequence repeatable; it is not used for security.

namespace Backroom.Modules.Messaging.IntegrationTests;

/// <summary>
/// The incremental summary (<see cref="ReactionSummaryEditor"/>) against a full rebuild from the rows, after a long deterministic random
/// sequence of adds and removals with ties and out-of-order times. No database or host: the rows are a list, the rebuild is written
/// independently of the editor.
/// </summary>
public sealed class ReactionSummaryEditorTests
{
    private static readonly string[] s_emoji = ["\U0001F600", "\U0001F601", "\U0001F602", "❤️", "\U0001F44D", "\U0001F389"];

    [Fact]
    public void Editor_AfterARandomSequenceOfAddsAndRemovals_EqualsAFullRebuildAfterEveryStep()
    {
        var random = new Random(20261128);
        var accounts = Enumerable.Range(0, 9).Select(index => new Guid(random.Next(), (short)index, 7, [1, 2, 3, 4, 5, 6, 7, (byte)random.Next(256)])).ToArray();
        var origin = new DateTimeOffset(2026, 11, 28, 10, 0, 0, TimeSpan.Zero);
        var rows = new List<(Guid Account, string Emoji, DateTimeOffset At)>();
        IReadOnlyList<StoredReaction> summary = [];
        var refills = 0;

        for (var step = 0; step < 4000; step++)
        {
            var account = accounts[random.Next(accounts.Length)];
            var emoji = s_emoji[random.Next(s_emoji.Length)];
            var existing = rows.FindIndex(row => row.Account == account && row.Emoji == emoji);
            if (existing < 0)
            {
                // Times repeat and run backwards now and then (equal times, a node's clock a little behind).
                var at = origin.AddTicks(random.Next(0, 40) * 10);
                rows.Add((account, emoji, at));
                summary = Replace(summary, emoji, ReactionSummaryEditor.Added(Entry(summary, emoji), emoji, new Reactor(account, at)));
            }
            else
            {
                rows.RemoveAt(existing);
                IReadOnlyList<Reactor>? refill = null;
                if (ReactionSummaryEditor.NeedsRefill(Entry(summary, emoji), account))
                {
                    refills++;
                    refill = Earliest(rows.Where(row => row.Emoji == emoji).Select(row => new Reactor(row.Account, row.At)));
                }

                summary = Replace(summary, emoji, ReactionSummaryEditor.Removed(Entry(summary, emoji), account, refill));
            }

            Describe(summary).Should().Equal(Describe(Rebuild(rows)), $"step {step}");
        }

        refills.Should().BeGreaterThan(100, "the sequence must exercise the refill of a sample");
    }

    [Fact]
    public void Removed_ForAnEmojiWithoutAnEntry_LeavesNoEntry()
    {
        ReactionSummaryEditor.Removed(null, Guid.NewGuid(), null).Should().BeNull();
    }

    private static StoredReaction? Entry(IReadOnlyList<StoredReaction> summary, string emoji) =>
        summary.FirstOrDefault(entry => string.Equals(entry.Emoji, emoji, StringComparison.Ordinal));

    // What the store does on the server: replace (or drop) the emoji's entry, then order the entries by first reaction time and code point.
    private static IReadOnlyList<StoredReaction> Replace(IReadOnlyList<StoredReaction> summary, string emoji, StoredReaction? entry) =>
        [
            .. summary.Where(existing => !string.Equals(existing.Emoji, emoji, StringComparison.Ordinal))
                .Concat(entry is null ? [] : [entry])
                .OrderBy(existing => existing.Sample[0].At)
                .ThenBy(existing => existing.Emoji, StringComparer.Ordinal),
        ];

    private static List<Reactor> Earliest(IEnumerable<Reactor> reactors) =>
        [.. reactors.OrderBy(reactor => reactor.At).ThenBy(reactor => reactor.AccountId.ToString("N"), StringComparer.Ordinal).Take(3)];

    // The full rebuild: group the rows by emoji, the earliest three reactors and the count, emoji by first reaction time then code point.
    private static List<StoredReaction> Rebuild(List<(Guid Account, string Emoji, DateTimeOffset At)> rows) =>
        [
            .. rows.GroupBy(row => row.Emoji, StringComparer.Ordinal)
                .Select(group => new StoredReaction(group.Key, group.Count(), Earliest(group.Select(row => new Reactor(row.Account, row.At)))))
                .OrderBy(entry => entry.Sample[0].At)
                .ThenBy(entry => entry.Emoji, StringComparer.Ordinal),
        ];

    private static List<string> Describe(IReadOnlyList<StoredReaction> summary) =>
        [.. summary.Select(entry => string.Create(
            CultureInfo.InvariantCulture,
            $"{entry.Emoji}|{entry.Count}|{string.Join(',', entry.Sample.Select(reactor => $"{reactor.AccountId:N}@{reactor.At.UtcTicks}"))}"))];
}
