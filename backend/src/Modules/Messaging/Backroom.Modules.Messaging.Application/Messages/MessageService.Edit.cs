using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Backroom.BuildingBlocks.Domain.Errors;
using Backroom.Modules.Messaging.Application.Channels;
using Backroom.Modules.Messaging.Application.Conversations;
using Backroom.Modules.Messaging.Contracts;
using Backroom.Modules.Permissions.Contracts;
using Backroom.Modules.Realtime.Contracts;

namespace Backroom.Modules.Messaging.Application.Messages;

/// <summary>
/// Edit and delete (docs/contracts/phase-2.md, flow 2 "Edit and delete"; HTTP <c>PATCH</c> and <c>DELETE
/// .../messages/{id}</c>). Unlimited in time. An edit is the author's only; a delete is the author's, or a moderator's
/// (<c>messages.remove-others-public</c>) in a public channel only. A message the account cannot read is
/// <c>404 messaging.message_not_found</c> before anything else is checked. Both take the conversation's next change
/// number (so they need the confirmed epoch), keep it as the message's <c>version_seq</c>, write their integration
/// event in the same unit of work and announce themselves to the conversation's group after the commit. A delete erases
/// the text, the blocks and the message's mention and link rows in one statement and leaves the tombstone; an edit
/// rewrites the mention and link rows to match the new blocks. No edit history is kept.
/// </summary>
internal sealed partial class MessageService
{
    public static readonly Error MessageNotFound =
        Error.NotFound("messaging.message_not_found", "There is no such message.");

    public static readonly Error NotAuthor =
        Error.Forbidden("messaging.not_author", "Only the author can edit a message.");

    public static readonly Error MessageDeleted =
        Error.Conflict("messaging.message_deleted", "The message has been deleted.");

    public static readonly Error EditUnavailable =
        Error.Forbidden("messaging.message_edit_unavailable", "Editing and deleting messages is not available yet.");

    /// <summary>Rewrites the message's content with <paramref name="blocks"/> (the same closed schema as a send). The author only.</summary>
    public async Task<Result<SentMessage>> EditAsync(Guid accountId, Guid messageId, JsonElement? blocks, CancellationToken cancellationToken)
    {
        if (!messageEdit.Enabled)
        {
            return EditUnavailable;
        }

        // Everything that needs no data first. Blocks are a closed schema: nothing is dropped silently.
        if (blocks is not { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } received)
        {
            return MessageInvalid.WithReason("blocks");
        }

        if (MessageBlocks.Parse(received, out var parsed) is { } refused)
        {
            return refused;
        }

        var organizationId = OrganizationId;
        if (await messages.FindHeadAsync(messageId, cancellationToken) is not { } head
            || await conversations.FindAsync(organizationId, head.ConversationId, accountId, cancellationToken) is not { CanRead: true } access)
        {
            return MessageNotFound;
        }

        if (head.AuthorAccountId != accountId)
        {
            return NotAuthor;
        }

        if (head.Deleted)
        {
            return MessageDeleted;
        }

        // Writes need the right to post: an open membership in a conversation that is not archived.
        if (access.Archived)
        {
            return MembershipService.ConversationArchived;
        }

        if (!access.IsMember)
        {
            return NotMember;
        }

        if (await EpochAsync(cancellationToken) is not { } epoch)
        {
            return EpochUnconfirmed;
        }

        var (refusal, text, readers) = await ResolveContentAsync(organizationId, head.ConversationId, parsed!, cancellationToken);
        if (refusal is not null)
        {
            return refusal;
        }

        var now = StoredTime.Now(timeProvider);

        // The row lock: the change number follows commit order. The message is written under it with a deleted check (a delete may have won).
        var change = await conversations.TouchAsync(organizationId, head.ConversationId, cancellationToken);
        var json = MessageBlocks.ToJson(parsed!.Blocks);
        if (await messages.EditAsync(messageId, change.ChangeSeq, text, json, now, cancellationToken) is not { } edited)
        {
            return MessageDeleted;
        }

        // The rows of the old content go, the rows of the new content come; a person mentioned before is not added again.
        var before = await messages.DeleteMentionsAsync(messageId, cancellationToken);
        await messages.DeleteLinksAsync(messageId, cancellationToken);
        if (readers.Count > 0)
        {
            await messages.AddMentionsAsync(organizationId, head.ConversationId, messageId, readers, cancellationToken);
        }

        if (parsed.Links.Count > 0)
        {
            await messages.AddLinksAsync(organizationId, head.ConversationId, messageId, parsed.Links, now, cancellationToken);
        }

        var added = readers.Where(reader => !before.Contains(reader)).ToArray();
        await outbox.AddAsync(
            new MessageEdited(ids.NewId(), organizationId, now, head.ConversationId, messageId, change.ChangeSeq, added),
            cancellationToken);
        liveEvents.Add(new MessageEditedPayload(
            organizationId, head.ConversationId, messageId, change.ChangeSeq, change.ChangeSeq, MessageBlocks.ToLive(parsed.Blocks), text,
            now.ToString("O", CultureInfo.InvariantCulture), epoch));

        LogEdited(logger, messageId, head.ConversationId, organizationId, accountId);
        return new SentMessage(edited, Created: false, epoch);
    }

    /// <summary>
    /// Deletes the message, leaving its tombstone. The author may delete while they can read the conversation, also in an archived
    /// one and without a membership (a public channel stays readable after leaving it); after leaving a private channel or a DM the
    /// message is unreachable (404), which is a follow-up for personal data erasure. A moderator needs
    /// <c>messages.remove-others-public</c> and a public channel, archived ones included (moderation must work in archives).
    /// Deleting a deleted message changes nothing.
    /// </summary>
    public async Task<Result> DeleteAsync(Guid accountId, Guid messageId, CancellationToken cancellationToken)
    {
        if (!messageEdit.Enabled)
        {
            return EditUnavailable;
        }

        var organizationId = OrganizationId;
        if (await messages.FindHeadAsync(messageId, cancellationToken) is not { } head
            || await conversations.FindAsync(organizationId, head.ConversationId, accountId, cancellationToken) is not { CanRead: true } access)
        {
            return MessageNotFound;
        }

        var isAuthor = head.AuthorAccountId == accountId;
        if (!isAuthor
            && !(access.IsPublicChannel && await permissions.HasPermissionAsync(accountId, SystemRoles.MessagesRemoveOthersPublic, cancellationToken)))
        {
            return ChannelService.Forbidden;
        }

        if (head.Deleted)
        {
            return Result.Success;
        }

        if (await EpochAsync(cancellationToken) is not { } epoch)
        {
            return EpochUnconfirmed;
        }

        var now = StoredTime.Now(timeProvider);
        var change = await conversations.TouchAsync(organizationId, head.ConversationId, cancellationToken);
        if (!await messages.DeleteAsync(messageId, change.ChangeSeq, now, cancellationToken))
        {
            // Deleted by someone else between the check and the lock: nothing left to do (the number taken stays unused).
            return Result.Success;
        }

        await outbox.AddAsync(
            new MessageDeleted(ids.NewId(), organizationId, now, head.ConversationId, messageId, isAuthor ? null : accountId),
            cancellationToken);
        liveEvents.Add(new MessageDeletedPayload(organizationId, head.ConversationId, messageId, change.ChangeSeq, change.ChangeSeq, epoch));

        LogDeleted(logger, messageId, head.ConversationId, organizationId, accountId);
        return Result.Success;
    }

    /// <summary>
    /// What the server derives from validated blocks: the plain text (names looked up for mentions, trimmed and normalized once
    /// as a whole) and the mentioned accounts that can read the conversation, who get a mention row. Every mentioned account
    /// must be an active member.
    /// </summary>
    private async Task<(Error? Refusal, string Text, IReadOnlyList<Guid> Readers)> ResolveContentAsync(
        Guid organizationId, Guid conversationId, ParsedBlocks parsed, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<Guid, string> names = s_noNames;
        IReadOnlyList<Guid> readers = [];
        if (parsed.MentionedAccountIds.Count > 0)
        {
            names = await memberNames.FindActiveAsync(parsed.MentionedAccountIds, cancellationToken);
            if (names.Count != parsed.MentionedAccountIds.Count)
            {
                return (MessageBlocks.MentionInvalid, string.Empty, readers);
            }

            readers = await conversations.FilterReadersAsync(organizationId, conversationId, parsed.MentionedAccountIds, cancellationToken);
        }

        // Trimmed and normalized once, as a whole; the 4,000 characters are counted on the result.
        var derived = MessageBlocks.DeriveText(parsed, names);
        return MessageText.TryPrepare(derived, out var prepared)
            ? (null, prepared, readers)
            : (MessageInvalid.WithReason(string.IsNullOrWhiteSpace(derived) ? "text.empty" : "text.length"), string.Empty, readers);
    }

    // Ids only: never the text.
    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "Message {MessageId} in conversation {ConversationId} of organization {OrganizationId} edited by account {AccountId}.")]
    private static partial void LogEdited(ILogger logger, Guid messageId, Guid conversationId, Guid organizationId, Guid accountId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "Message {MessageId} in conversation {ConversationId} of organization {OrganizationId} deleted by account {AccountId}.")]
    private static partial void LogDeleted(ILogger logger, Guid messageId, Guid conversationId, Guid organizationId, Guid accountId);
}
