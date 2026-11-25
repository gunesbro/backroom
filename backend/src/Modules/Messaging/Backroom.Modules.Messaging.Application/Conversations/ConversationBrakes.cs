using Backroom.BuildingBlocks.Domain.Errors;

namespace Backroom.Modules.Messaging.Application.Conversations;

/// <summary>
/// The hourly brakes of flow 2 ("Rate limits") on conversations: per account and organization at most
/// <see cref="ChannelsPerHour"/> new channels and <see cref="MemberAdditionsPerHour"/> people added to conversations.
/// Counted from the stored rows in the request's transaction (an index probe each), so every node agrees and a refused
/// request counts nothing; concurrent requests of one person can pass together, which the limits tolerate.
/// </summary>
internal static class ConversationBrakes
{
    public const int ChannelsPerHour = 20;
    public const int MemberAdditionsPerHour = 200;

    public const string RateLimitedCode = "messaging.rate_limited";

    private static readonly TimeSpan s_window = TimeSpan.FromHours(1);

    public static async Task<Error?> CheckNewChannelAsync(
        IConversationStore conversations, Guid organizationId, Guid accountId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var recent = await conversations.RecentChannelCreationsAsync(organizationId, accountId, now - s_window, ChannelsPerHour, cancellationToken);
        return recent.Count >= ChannelsPerHour ? RateLimited(recent.Oldest, now) : null;
    }

    public static async Task<Error?> CheckMemberAdditionsAsync(
        IConversationStore conversations, Guid organizationId, Guid accountId, int adding, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (adding == 0)
        {
            return null;
        }

        var recent = await conversations.RecentMemberAdditionsAsync(organizationId, accountId, now - s_window, MemberAdditionsPerHour, cancellationToken);
        return recent.Count + adding > MemberAdditionsPerHour ? RateLimited(recent.Oldest, now) : null;
    }

    // Free again when the oldest counted action leaves the window.
    private static Error RateLimited(DateTimeOffset? oldest, DateTimeOffset now)
    {
        var retryAfter = oldest is { } at ? at + s_window - now : s_window;
        return Error.RateLimited(
            RateLimitedCode, "Too many changes to conversations; try again later.", retryAfter < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : retryAfter);
    }
}
