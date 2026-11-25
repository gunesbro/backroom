using Backroom.BuildingBlocks.Application.Organizations;
using Backroom.BuildingBlocks.Domain.Errors;
using Backroom.Modules.Messaging.Contracts;

namespace Backroom.Modules.Notifications.Application.Settings;

/// <summary>
/// The signed-in person's notification settings (docs/contracts/phase-2.md, flow 8 and "HTTP APIs"). The endpoint has
/// checked the organization membership; a conversation's settings also need read access to that conversation.
/// </summary>
internal sealed class SettingsService(
    INotificationStore store,
    IConversationReadAccess conversations,
    IOrganizationContext context,
    TimeProvider timeProvider)
{
    /// <summary>The longest organization mute (a bound, not a product option).</summary>
    public static readonly TimeSpan MaxMute = TimeSpan.FromDays(366);

    public static readonly Error LevelInvalid =
        Error.Validation("notifications.level_invalid", "The level must be all, mentions or none.");

    public static readonly Error MuteInvalid =
        Error.Validation("notifications.mute_invalid", "The mute must end within the next year.");

    public static readonly Error ConversationNotFound =
        Error.NotFound("notifications.conversation_not_found", "The conversation was not found.");

    public async Task<AccountSettings> GetAccountAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var stored = await store.GetAccountSettingsAsync([accountId], cancellationToken);
        return stored.TryGetValue(accountId, out var settings) ? settings : AccountSettings.Default;
    }

    public async Task<Result<AccountSettings>> PutAccountAsync(
        Guid accountId, string? defaultLevel, DateTimeOffset? orgMutedUntil, bool sound, bool hidePreviews, CancellationToken cancellationToken)
    {
        if (!NotificationLevelNames.TryParse(defaultLevel, out var level))
        {
            return LevelInvalid;
        }

        var now = timeProvider.GetUtcNow();
        if (orgMutedUntil is { } until && until > now + MaxMute)
        {
            return MuteInvalid;
        }

        var settings = new AccountSettings(level, orgMutedUntil?.ToUniversalTime(), sound, hidePreviews);
        await store.SetAccountSettingsAsync(OrganizationId, accountId, settings, cancellationToken);
        return settings;
    }

    /// <summary>The stored settings, or <see langword="null"/> while the conversation has none (the account settings apply).</summary>
    public async Task<Result<ConversationSettings?>> GetConversationAsync(Guid accountId, Guid conversationId, CancellationToken cancellationToken)
    {
        if (!await conversations.CanReadAsync(OrganizationId, conversationId, accountId, cancellationToken))
        {
            return ConversationNotFound;
        }

        var stored = await store.GetConversationSettingsAsync(conversationId, [accountId], cancellationToken);
        return Result<ConversationSettings?>.Success(stored.TryGetValue(accountId, out var settings) ? settings : null);
    }

    public async Task<Result<ConversationSettings>> PutConversationAsync(
        Guid accountId, Guid conversationId, string? level, bool followedThreadReplies, bool ignoreGroupMentions, bool sound, CancellationToken cancellationToken)
    {
        if (!NotificationLevelNames.TryParse(level, out var parsed))
        {
            return LevelInvalid;
        }

        if (!await conversations.CanReadAsync(OrganizationId, conversationId, accountId, cancellationToken))
        {
            return ConversationNotFound;
        }

        var settings = new ConversationSettings(parsed, followedThreadReplies, ignoreGroupMentions, sound);
        await store.SetConversationSettingsAsync(OrganizationId, accountId, conversationId, settings, cancellationToken);
        return settings;
    }

    private Guid OrganizationId =>
        context.OrganizationId ?? throw new InvalidOperationException("The request has not entered an organization.");
}
