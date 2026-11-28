using Microsoft.Extensions.Logging;
using Backroom.BuildingBlocks.Domain.Errors;
using Backroom.Modules.Messaging.Application.Conversations;
using Backroom.Modules.Realtime.Contracts;

namespace Backroom.Modules.Messaging.Application.Messages;

/// <summary>
/// Reactions (docs/contracts/phase-2.md, flow 2 "Reactions"; HTTP <c>PUT</c> and <c>DELETE .../messages/{id}/reactions/{emoji}</c>).
/// Adding needs read access and the right to post (an open membership in a conversation that is not archived), like an edit; removing one's own
/// reaction needs read access only (it works after leaving a public channel and in an archived conversation); an unknown
/// message, one the caller cannot read and a deleted one are the same <c>404 messaging.message_not_found</c>. Both are idempotent:
/// adding a reaction that exists and removing one that does not change nothing, take no change number and announce nothing. A real
/// change takes the conversation's next change number as the message's <c>version_seq</c> (so sync sees it), updates the message's
/// stored summary (by this one reaction, see <see cref="ReactionSummaryEditor"/>) and announces <c>ReactionsChanged</c> to the conversation's group after the commit. No integration event: the
/// contract lists none for reactions. Reactions of a message are erased by its delete.
/// <para>
/// The limits (50 distinct emoji per message, 20 reactions per person per message) are checked under the conversation row lock,
/// which every change to the conversation's messages takes, so concurrent requests cannot together exceed them.
/// </para>
/// </summary>
internal sealed partial class MessageService
{
    public const int MaxReactionsPerMessage = 50;

    public const int MaxReactionsPerPerson = 20;

    public static readonly Error ReactionInvalid =
        Error.Validation("messaging.reaction_invalid", "A reaction is exactly one emoji of at most 32 bytes.");

    public static readonly Error ReactionLimit =
        Error.Conflict("messaging.reaction_limit", "A message takes at most 50 different emoji and a person at most 20 reactions on it.");

    public static readonly Error ReactionsUnavailable =
        Error.NotFound("messaging.reactions_unavailable", "Reactions are not available yet.");

    /// <summary>Adds the account's reaction to the message.</summary>
    public Task<Result> AddReactionAsync(Guid accountId, Guid messageId, string? emoji, CancellationToken cancellationToken) =>
        ChangeReactionAsync(accountId, messageId, emoji, add: true, cancellationToken);

    /// <summary>Removes the account's reaction from the message.</summary>
    public Task<Result> RemoveReactionAsync(Guid accountId, Guid messageId, string? emoji, CancellationToken cancellationToken) =>
        ChangeReactionAsync(accountId, messageId, emoji, add: false, cancellationToken);

    private async Task<Result> ChangeReactionAsync(Guid accountId, Guid messageId, string? input, bool add, CancellationToken cancellationToken)
    {
        if (!messageReactions.Enabled)
        {
            return ReactionsUnavailable;
        }

        // Everything that needs no data first.
        if (!ReactionEmoji.TryRead(input, out var emoji))
        {
            return ReactionInvalid;
        }

        var organizationId = OrganizationId;
        if (await messages.FindHeadAsync(messageId, cancellationToken) is not { Deleted: false } head
            || await conversations.FindAsync(organizationId, head.ConversationId, accountId, cancellationToken) is not { CanRead: true } access)
        {
            return MessageNotFound;
        }

        // Adding needs the right to post. Removing one's own reaction needs read access only: it must work after leaving a public
        // channel and in an archived conversation.
        if (add && access.Archived)
        {
            return MembershipService.ConversationArchived;
        }

        if (add && !access.IsMember)
        {
            return NotMember;
        }

        if (await EpochAsync(cancellationToken) is not { } epoch)
        {
            return EpochUnconfirmed;
        }

        // The row lock first: the limits and the change number then follow commit order. What was checked above is checked again,
        // because a delete, an archive or a removal that committed in between is visible now.
        if (await conversations.GetAsync(organizationId, head.ConversationId, accountId, forUpdate: true, cancellationToken) is not { IsVisible: true } locked)
        {
            return MessageNotFound;
        }

        if (await messages.FindHeadAsync(messageId, cancellationToken) is not { Deleted: false })
        {
            return MessageNotFound;
        }

        if (add && locked.Archived)
        {
            return MembershipService.ConversationArchived;
        }

        if (add && !locked.IsMember)
        {
            return NotMember;
        }

        var now = StoredTime.Now(timeProvider);
        if (add)
        {
            switch (await messages.AddReactionAsync(organizationId, messageId, accountId, emoji, now, cancellationToken))
            {
                case ReactionAdd.Exists:
                    return Result.Success;
                case ReactionAdd.PersonLimit or ReactionAdd.EmojiLimit:
                    return ReactionLimit;
            }
        }
        else if (!await messages.RemoveReactionAsync(messageId, accountId, emoji, cancellationToken))
        {
            return Result.Success;
        }

        // A real change: only this emoji's entry of the summary changes (never rebuilt from all the rows: with thousands of reactions that
        // would hold the conversation lock for every change), then the next change number becomes the message's version.
        var current = await messages.ReadReactionEntryAsync(messageId, emoji, cancellationToken);
        StoredReaction? updated;
        if (add)
        {
            updated = ReactionSummaryEditor.Added(current, emoji, new Reactor(accountId, now));
        }
        else
        {
            var refill = ReactionSummaryEditor.NeedsRefill(current, accountId)
                ? await messages.ListEarliestReactorsAsync(messageId, emoji, cancellationToken)
                : null;
            updated = ReactionSummaryEditor.Removed(current, accountId, refill);
        }

        var change = await conversations.TouchAsync(organizationId, head.ConversationId, cancellationToken);
        var summary = await messages.ReplaceReactionEntryAsync(messageId, change.ChangeSeq, emoji, updated, cancellationToken);
        liveEvents.Add(new ReactionsChangedPayload(
            organizationId, head.ConversationId, messageId, change.ChangeSeq, change.ChangeSeq,
            [.. summary.Select(reaction => new LiveReaction(reaction.Emoji, reaction.Count, reaction.Sample))], epoch));

        LogReactionsChanged(logger, messageId, head.ConversationId, organizationId, accountId);
        return Result.Success;
    }

    /// <summary>
    /// The messages with their reactions as <paramref name="accountId"/> sees them: with the flag off none, otherwise the stored summary
    /// and <c>reactedByMe</c> from one statement for the whole page (none when no message of it has a reaction). Tombstones carry none.
    /// </summary>
    private async Task<IReadOnlyList<MessageEntry>> WithReactionsAsync(Guid accountId, IReadOnlyList<MessageEntry> entries, CancellationToken cancellationToken)
    {
        if (!messageReactions.Enabled)
        {
            return entries.Any(entry => entry.Reactions is not null) ? [.. entries.Select(entry => entry with { Reactions = null })] : entries;
        }

        var reacted = entries.Where(entry => entry.Reactions is { Count: > 0 }).Select(entry => entry.Id).ToArray();
        var mine = reacted.Length == 0
            ? []
            : (await messages.ListReactedByAsync(reacted, accountId, cancellationToken)).ToHashSet();
        return
        [
            .. entries.Select(entry => entry.Reactions is { Count: > 0 } summary
                ? entry with { Reactions = [.. summary.Select(reaction => reaction with { ReactedByMe = mine.Contains((entry.Id, reaction.Emoji)) })] }
                : entry),
        ];
    }

    private async Task<MessageEntry> WithReactionsAsync(Guid accountId, MessageEntry entry, CancellationToken cancellationToken) =>
        (await WithReactionsAsync(accountId, [entry], cancellationToken))[0];

    // Ids only.
    [LoggerMessage(EventId = 4, Level = LogLevel.Debug, Message = "Reactions of message {MessageId} in conversation {ConversationId} of organization {OrganizationId} changed by account {AccountId}.")]
    private static partial void LogReactionsChanged(ILogger logger, Guid messageId, Guid conversationId, Guid organizationId, Guid accountId);
}
