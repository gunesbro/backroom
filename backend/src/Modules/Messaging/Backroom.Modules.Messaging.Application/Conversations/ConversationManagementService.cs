using System.Globalization;
using Microsoft.Extensions.Logging;
using Backroom.BuildingBlocks.Application.Cells;
using Backroom.BuildingBlocks.Application.Identifiers;
using Backroom.BuildingBlocks.Application.Messaging;
using Backroom.BuildingBlocks.Application.Organizations;
using Backroom.BuildingBlocks.Domain.Errors;
using Backroom.Modules.Messaging.Application.Channels;
using Backroom.Modules.Messaging.Application.Live;
using Backroom.Modules.Messaging.Application.Messages;
using Backroom.Modules.Messaging.Contracts;
using Backroom.Modules.Permissions.Contracts;
using Backroom.Modules.Realtime.Contracts;

namespace Backroom.Modules.Messaging.Application.Conversations;

/// <summary>
/// One conversation and its management (docs/contracts/phase-2.md, flow 1; HTTP <c>GET</c>/<c>PATCH
/// .../conversations/{id}</c>, <c>/archive</c>, <c>/unarchive</c>, <c>GET .../private-channels/count</c>). The endpoint
/// has checked the organization membership. A public channel is managed with <c>channels.manage-public</c>; a private
/// channel by its owner and by nobody else: no role, Owner included, is asked. A conversation the account may not know
/// of is <see cref="MembershipService.ConversationNotFound"/> before anything else is checked. Changes lock the
/// conversation row, take the next change number and write their integration event (ids only) in the same unit of
/// work.
/// </summary>
internal sealed partial class ConversationManagementService(
    IConversationStore conversations,
    IOrganizationPermissionCheck permissions,
    ICellEpoch cellEpoch,
    IOutbox outbox,
    AfterCommitLiveEvents liveEvents,
    IIdGenerator ids,
    IOrganizationContext context,
    TimeProvider timeProvider,
    ILogger<ConversationManagementService> logger)
{
    public static readonly Error UpdateInvalid =
        Error.Validation("messaging.conversation_update_invalid", "Give a name, a description or both.");

    public static readonly Error NotAChannel =
        Error.Conflict("messaging.not_a_channel", "Only channels have a name, a description and an archive.");

    public async Task<Result<ConversationDetails>> GetAsync(Guid accountId, Guid conversationId, CancellationToken cancellationToken) =>
        await conversations.GetAsync(OrganizationId, conversationId, accountId, forUpdate: false, cancellationToken) is { IsVisible: true } conversation
            ? conversation
            : MembershipService.ConversationNotFound;

    /// <summary>
    /// Renames the channel, sets its description, or both. An empty description removes it. A public channel's new
    /// name must be free among open public channels; private channels are never compared, in either direction.
    /// </summary>
    public async Task<Result<ConversationDetails>> UpdateAsync(Guid accountId, Guid conversationId, string? name, string? description, CancellationToken cancellationToken)
    {
        if (name is null && description is null)
        {
            return UpdateInvalid;
        }

        string? newName = null;
        if (name is not null && !ChannelName.TryNormalize(name, out newName))
        {
            return ChannelService.NameInvalid;
        }

        if (!ChannelDescription.TryNormalize(description, out var newDescription))
        {
            return ChannelService.DescriptionInvalid;
        }

        var organizationId = OrganizationId;
        var conversation = await conversations.GetAsync(organizationId, conversationId, accountId, forUpdate: true, cancellationToken);
        if (await AuthorizeAsync(conversation, accountId, cancellationToken) is { } refused)
        {
            return refused;
        }

        if (conversation!.Archived)
        {
            return MembershipService.ConversationArchived;
        }

        if (await EpochAsync(cancellationToken) is not { } epoch)
        {
            return MessageService.EpochUnconfirmed;
        }

        var renamed = newName is not null && !string.Equals(newName, conversation.Name, StringComparison.Ordinal);
        var change = await conversations.UpdateAsync(
            organizationId, conversationId, renamed ? newName : null, renamed ? ChannelName.KeyOf(newName!) : null,
            description is not null, newDescription, conversation.IsPublicChannel, cancellationToken);
        if (change is null)
        {
            return ChannelService.NameTaken;
        }

        if (renamed)
        {
            await AnnounceAsync(organizationId, conversation, ConversationChanges.Renamed, accountId, cancellationToken);
        }

        Publish(organizationId, conversationId, change, epoch);
        return conversation with { Name = change.Name, Description = change.Description, ChangeSeq = change.ChangeSeq };
    }

    /// <summary>Archives the channel; archiving an archived one changes nothing.</summary>
    public async Task<Result> ArchiveAsync(Guid accountId, Guid conversationId, CancellationToken cancellationToken)
    {
        var organizationId = OrganizationId;
        var conversation = await conversations.GetAsync(organizationId, conversationId, accountId, forUpdate: true, cancellationToken);
        if (await AuthorizeAsync(conversation, accountId, cancellationToken) is { } refused)
        {
            return refused;
        }

        if (conversation!.Archived)
        {
            return Result.Success;
        }

        if (await EpochAsync(cancellationToken) is not { } epoch)
        {
            return MessageService.EpochUnconfirmed;
        }

        var change = await conversations.SetArchivedAsync(organizationId, conversationId, StoredTime.Now(timeProvider), cancellationToken)
            ?? throw new InvalidOperationException("Archiving never compares names.");
        await AnnounceAsync(organizationId, conversation, ConversationChanges.Archived, accountId, cancellationToken);
        Publish(organizationId, conversationId, change, epoch);
        LogArchived(logger, conversationId, organizationId, accountId);
        return Result.Success;
    }

    /// <summary>
    /// Reopens an archived channel; reopening an open one changes nothing. A public channel whose name an open public
    /// channel has taken meanwhile stays archived (<c>messaging.channel_name_taken</c>).
    /// </summary>
    public async Task<Result> UnarchiveAsync(Guid accountId, Guid conversationId, CancellationToken cancellationToken)
    {
        var organizationId = OrganizationId;
        var conversation = await conversations.GetAsync(organizationId, conversationId, accountId, forUpdate: true, cancellationToken);
        if (await AuthorizeAsync(conversation, accountId, cancellationToken) is { } refused)
        {
            return refused;
        }

        if (!conversation!.Archived)
        {
            return Result.Success;
        }

        if (await EpochAsync(cancellationToken) is not { } epoch)
        {
            return MessageService.EpochUnconfirmed;
        }

        if (await conversations.SetArchivedAsync(organizationId, conversationId, null, cancellationToken) is not { } change)
        {
            return ChannelService.NameTaken;
        }

        await AnnounceAsync(organizationId, conversation, ConversationChanges.Unarchived, accountId, cancellationToken);
        Publish(organizationId, conversationId, change, epoch);
        LogUnarchived(logger, conversationId, organizationId, accountId);
        return Result.Success;
    }

    /// <summary>
    /// The admins' whole view of private channels (owner decision 11): how many open ones the organization has. The
    /// endpoint has checked <c>channels.manage-public</c>.
    /// </summary>
    public Task<int> CountPrivateChannelsAsync(CancellationToken cancellationToken) =>
        conversations.CountOpenPrivateChannelsAsync(OrganizationId, cancellationToken);

    // Existence first: someone who may not know of the conversation learns nothing from the answer.
    private async Task<Error?> AuthorizeAsync(ConversationDetails? conversation, Guid accountId, CancellationToken cancellationToken)
    {
        if (conversation is not { IsVisible: true })
        {
            return MembershipService.ConversationNotFound;
        }

        if (conversation.IsPrivateChannel)
        {
            return conversation.IsOwner ? null : MembershipService.NotOwner;
        }

        if (!conversation.IsPublicChannel)
        {
            return NotAChannel;
        }

        return await permissions.HasPermissionAsync(accountId, SystemRoles.ChannelsManagePublic, cancellationToken) ? null : ChannelService.Forbidden;
    }

    private async Task AnnounceAsync(Guid organizationId, ConversationDetails conversation, string change, Guid accountId, CancellationToken cancellationToken) =>
        await outbox.AddAsync(
            new ConversationChanged(ids.NewId(), organizationId, StoredTime.Now(timeProvider), conversation.Id, conversation.Kind, change, accountId, conversation.MemberCount),
            cancellationToken);

    // To the conversation's group: for a private channel that is its subscribed members and nobody else.
    private void Publish(Guid organizationId, Guid conversationId, ConversationChange change, string epoch) =>
        liveEvents.Add(new ConversationUpdatedPayload(
            organizationId, conversationId, change.Name, change.Description,
            change.ArchivedAt?.ToString("O", CultureInfo.InvariantCulture), change.MemberCount, change.ChangeSeq, epoch));

    private async ValueTask<string?> EpochAsync(CancellationToken cancellationToken) =>
        await cellEpoch.ConfirmedEpochAsync(cancellationToken) is { } epoch ? epoch.ToString(CultureInfo.InvariantCulture) : null;

    private Guid OrganizationId =>
        context.OrganizationId ?? throw new InvalidOperationException("The request has not entered an organization.");

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Conversation {ConversationId} of organization {OrganizationId} archived by account {AccountId}.")]
    private static partial void LogArchived(ILogger logger, Guid conversationId, Guid organizationId, Guid accountId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Conversation {ConversationId} of organization {OrganizationId} unarchived by account {AccountId}.")]
    private static partial void LogUnarchived(ILogger logger, Guid conversationId, Guid organizationId, Guid accountId);
}
