using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Backroom.BuildingBlocks.Application.Cells;
using Backroom.BuildingBlocks.Application.Identifiers;
using Backroom.BuildingBlocks.Application.Messaging;
using Backroom.BuildingBlocks.Application.Organizations;
using Backroom.BuildingBlocks.Domain.Errors;
using Backroom.Modules.Messaging.Application.Channels;
using Backroom.Modules.Messaging.Application.Conversations;
using Backroom.Modules.Messaging.Application.Live;
using Backroom.Modules.Messaging.Contracts;
using Backroom.Modules.Organization.Contracts;
using Backroom.Modules.Realtime.Contracts;

namespace Backroom.Modules.Messaging.Application.Messages;

/// <summary>
/// Messages, plain and rich text (docs/contracts/phase-1.md, flow 4, steps 2 and 3; docs/contracts/phase-2.md, flow 2). The endpoint has checked the organization
/// membership; reading and writing also need read access to the conversation (<see cref="ConversationAccess.CanRead"/>:
/// every active member for an open public channel, open members for every other kind), checked first, so nothing,
/// not even an idempotent repeat, answers for a conversation the account cannot read. Posting also needs an open
/// membership (docs/contracts/phase-2.md, "HTTP APIs": a public channel is joined first). The message and
/// its outbox row are written in the request unit of work; the live event is held until it commits.
/// <para>
/// Sequence numbers (docs/contracts/phase-2.md, "Sequence numbers"; docs/adr/0011): every answer with a number carries
/// the cell's confirmed epoch, and a read that presents a cursor is refused with <see cref="ResyncRequired"/> when its
/// epoch is missing or different, or when the cursor is ahead of the conversation's change counter (the client saw
/// numbers this database does not have: a restore or a failover).
/// </para>
/// </summary>
internal sealed partial class MessageService(
    IMessageStore messages,
    IChannelStore channels,
    IConversationStore conversations,
    IOutbox outbox,
    AfterCommitLiveEvents liveEvents,
    IIdGenerator ids,
    IOrganizationContext context,
    ICellEpoch cellEpoch,
    MessageSync sync,
    RichText richText,
    IMemberNames memberNames,
    TimeProvider timeProvider,
    ILogger<MessageService> logger)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;

    /// <summary>The longest epoch a client may present (docs/contracts/phase-2.md, "Sequence numbers").</summary>
    public const int MaxEpochLength = 32;

    /// <summary>The kind of a plain message in live payloads (docs/contracts/phase-2.md, "Data": <c>text|poll|thread_broadcast</c>).</summary>
    private const string TextKind = "text";

    private static readonly Dictionary<Guid, string> s_noNames = [];

    public static readonly Error ConversationNotFound = MembershipService.ConversationNotFound;

    public static readonly Error NotMember =
        Error.Forbidden("messaging.not_a_member", "Join the channel to post in it.");

    public static readonly Error MessageInvalid =
        Error.Validation("messaging.message_invalid", "The message needs a client message id and valid text or blocks of 1 to 4000 characters; the reason names what was refused.");

    public static readonly Error PageInvalid =
        Error.Validation("messaging.page_invalid", "Use only one of before, after and changed_after, a cursor of 0 or more, and a page size between 1 and 100.");

    public static readonly Error ResyncRequired =
        Error.Conflict("messaging.resync_required", "The cursor does not belong to this cell's current state; run a full resync.");

    /// <summary>The cell has no confirmed epoch, so no sequence number can be handed out; the endpoint answers 503.</summary>
    public static readonly Error EpochUnconfirmed =
        Error.Unexpected("cell.epoch_unconfirmed", "The cell has not confirmed its epoch with the control plane yet.");

    /// <summary>
    /// Sends a message: rich text as <paramref name="blocks"/> (validated against the closed schema; the text, the
    /// mention rows and the link rows are derived from them, and a <paramref name="text"/> sent along is not read), or
    /// plain <paramref name="text"/> (the blocks are derived: one paragraph).
    /// </summary>
    public async Task<Result<SentMessage>> SendAsync(
        Guid authorAccountId, Guid conversationId, Guid? clientMessageId, string? text, JsonElement? blocks, CancellationToken cancellationToken)
    {
        var organizationId = OrganizationId;
        if (clientMessageId is not { } clientId || clientId == Guid.Empty)
        {
            return MessageInvalid.WithReason("client_message_id");
        }

        // Everything that needs no data is checked first. Blocks are a closed schema: nothing is dropped silently.
        ParsedBlocks? parsed = null;
        string? prepared = null;
        if (blocks is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } received)
        {
            if (!richText.Enabled)
            {
                return MessageInvalid.WithReason("blocks.disabled");
            }

            if (MessageBlocks.Parse(received, out parsed) is { } refused)
            {
                return refused;
            }
        }
        else if (!MessageText.TryPrepare(text, out prepared))
        {
            return MessageInvalid.WithReason("text");
        }

        var access = await conversations.FindAsync(organizationId, conversationId, authorAccountId, cancellationToken);
        if (access is not { CanRead: true })
        {
            return ConversationNotFound;
        }

        // Archived conversations are read-only.
        if (access.Archived)
        {
            return MembershipService.ConversationArchived;
        }

        if (!access.IsMember)
        {
            return NotMember;
        }

        // Answered from the write fence's cached lookup: no statement of its own on the send path.
        if (await EpochAsync(cancellationToken) is not { } epoch)
        {
            return EpochUnconfirmed;
        }

        // A repeat (a retry after a lost answer) is answered before any lock is taken. This and the unique index rely on
        // READ COMMITTED (the default, checked by a test): a concurrent repeat that waits for the first send to commit
        // then sees its row.
        if (await messages.FindAsync(conversationId, authorAccountId, clientId, cancellationToken) is { } stored)
        {
            return new SentMessage(stored, Created: false, epoch);
        }

        // Mentions (docs/contracts/phase-2.md, flow 2): every mentioned account must be an active member, whose name
        // goes into the derived text. A mention row is written only for those who can read the conversation, so a
        // person mentioned in a conversation they are not in gets nothing from it. Both reads run before the row lock.
        IReadOnlyList<Guid> mentionedReaders = [];
        if (parsed is null)
        {
            prepared = prepared ?? throw new InvalidOperationException("A send without blocks has a prepared text.");
            parsed = MessageBlocks.FromPlainText(prepared);
        }
        else
        {
            IReadOnlyDictionary<Guid, string> names = s_noNames;
            if (parsed.MentionedAccountIds.Count > 0)
            {
                names = await memberNames.FindActiveAsync(parsed.MentionedAccountIds, cancellationToken);
                if (names.Count != parsed.MentionedAccountIds.Count)
                {
                    return MessageBlocks.MentionInvalid;
                }

                mentionedReaders = await conversations.FilterReadersAsync(organizationId, conversationId, parsed.MentionedAccountIds, cancellationToken);
            }

            // Trimmed and normalized once, as a whole; the 4,000 characters are counted on the result.
            var derived = MessageBlocks.DeriveText(parsed, names);
            if (!MessageText.TryPrepare(derived, out prepared))
            {
                return MessageInvalid.WithReason(string.IsNullOrWhiteSpace(derived) ? "text.empty" : "text.length");
            }
        }

        var now = StoredTime.Now(timeProvider);

        // Takes the conversation row lock until commit: the change counter follows commit order, which catch-up relies
        // on (docs/adr/0012). A new message's number is its seq, its version_seq and the conversation's last_message_seq.
        if (await channels.NextSequenceAsync(organizationId, conversationId, now, cancellationToken) is not { } seq)
        {
            return ConversationNotFound;
        }

        var message = new NewMessage(ids.NewId(), organizationId, conversationId, seq, authorAccountId, clientId, prepared, MessageBlocks.ToJson(parsed.Blocks), now);
        if (await messages.AddAsync(message, cancellationToken) is not { } storedBlocks)
        {
            // Another send of the same client message id committed between the check above and the lock; the first stays
            // (the change number taken here is left unused: sequences have gaps, clients never assume otherwise). The
            // statement that took it also announced a newest message that was not stored: put that back, under the row
            // lock this transaction still holds, so last_message_seq always names a stored message.
            await channels.RestoreLastMessageAsync(organizationId, conversationId, cancellationToken);
            var existing = await messages.FindAsync(conversationId, authorAccountId, clientId, cancellationToken)
                ?? throw new InvalidOperationException("The message exists by its unique index but could not be read.");
            return new SentMessage(existing, Created: false, epoch);
        }

        if (mentionedReaders.Count > 0)
        {
            await messages.AddMentionsAsync(organizationId, conversationId, message.Id, mentionedReaders, cancellationToken);
        }

        if (parsed.Links.Count > 0)
        {
            await messages.AddLinksAsync(organizationId, conversationId, message.Id, parsed.Links, now, cancellationToken);
        }

        await outbox.AddAsync(
            new MessagePosted(ids.NewId(), organizationId, now, conversationId, message.Id, authorAccountId, now),
            cancellationToken);
        if (sync.Enabled)
        {
            liveEvents.Add(new MessagePostedV2Payload(
                organizationId,
                conversationId,
                new LiveMessageV2(message.Id, seq, seq, TextKind, clientId, authorAccountId, MessageBlocks.ToLive(parsed.Blocks), prepared, now),
                seq,
                epoch));
        }
        else
        {
            liveEvents.Add(new MessagePostedPayload(organizationId, conversationId, new LiveMessage(message.Id, seq, clientId, authorAccountId, prepared, now, MessageBlocks.ToLive(parsed.Blocks))));
        }

        LogPosted(logger, message.Id, conversationId, organizationId, authorAccountId);
        return new SentMessage(new MessageEntry(message.Id, seq, seq, clientId, authorAccountId, prepared, storedBlocks, now, DeletedAt: null), Created: true, epoch);
    }

    /// <summary>
    /// One page: history by <paramref name="before"/> (newest first, the default) or <paramref name="after"/> (oldest
    /// first), or catch-up by <paramref name="changedAfter"/> (everything whose latest change is newer: new and changed
    /// messages and tombstones, in the order of the changes). Tombstones are part of every page.
    /// </summary>
    public async Task<Result<MessagePage>> ListAsync(
        Guid accountId, Guid conversationId, string? epoch, long? before, long? after, long? changedAfter, int? limit, CancellationToken cancellationToken)
    {
        var pageSize = limit ?? DefaultPageSize;
        var cursors = (before is null ? 0 : 1) + (after is null ? 0 : 1) + (changedAfter is null ? 0 : 1);
        if (pageSize is < 1 or > MaxPageSize || cursors > 1 || before < 0 || after < 0 || changedAfter < 0)
        {
            return PageInvalid;
        }

        // Before anything about the epoch: an unreadable conversation answers the same whatever the cursor says.
        var access = await conversations.FindAsync(OrganizationId, conversationId, accountId, cancellationToken);
        if (access is not { CanRead: true })
        {
            return ConversationNotFound;
        }

        if (await EpochAsync(cancellationToken) is not { } current)
        {
            return EpochUnconfirmed;
        }

        // A presented epoch must be the current one. A cursor needs one: always for changed_after (new with the epoch),
        // and for before and after once the flag is on (Phase 1 clients send none).
        var epochRequired = changedAfter is not null || (cursors == 1 && sync.Enabled);
        if (epoch is null ? epochRequired : epoch.Length > MaxEpochLength || !string.Equals(epoch, current, StringComparison.Ordinal))
        {
            return ResyncRequired;
        }

        // Every number a client holds was at most the counter when it was handed out, and the counter never goes back.
        // ("before" is exclusive, so the number after the newest change is still a cursor the server could have implied.)
        if (after > access.ChangeSeq || changedAfter > access.ChangeSeq || before - 1 > access.ChangeSeq)
        {
            return ResyncRequired;
        }

        // One more than asked for tells whether another page follows.
        IReadOnlyList<MessageEntry> found;
        if (changedAfter is { } changed)
        {
            found = await messages.ListChangedAfterAsync(conversationId, changed, pageSize + 1, cancellationToken);
            return found.Count > pageSize
                ? new MessagePage([.. found.Take(pageSize)], found[pageSize - 1].VersionSeq, current)
                : new MessagePage(found, null, current);
        }

        found = after is { } forward
            ? await messages.ListAfterAsync(conversationId, forward, pageSize + 1, cancellationToken)
            : await messages.ListBeforeAsync(conversationId, before ?? long.MaxValue, pageSize + 1, cancellationToken);
        return found.Count > pageSize
            ? new MessagePage([.. found.Take(pageSize)], found[pageSize - 1].Seq, current)
            : new MessagePage(found, null, current);
    }

    private async ValueTask<string?> EpochAsync(CancellationToken cancellationToken) =>
        await cellEpoch.ConfirmedEpochAsync(cancellationToken) is { } epoch ? epoch.ToString(CultureInfo.InvariantCulture) : null;

    private Guid OrganizationId =>
        context.OrganizationId ?? throw new InvalidOperationException("The request has not entered an organization.");

    // Ids only: never the text.
    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Message {MessageId} posted in conversation {ConversationId} of organization {OrganizationId} by account {AccountId}.")]
    private static partial void LogPosted(ILogger logger, Guid messageId, Guid conversationId, Guid organizationId, Guid accountId);
}
