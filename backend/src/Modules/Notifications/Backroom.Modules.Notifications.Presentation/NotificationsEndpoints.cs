using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Backroom.BuildingBlocks.Application.Accounts;
using Backroom.BuildingBlocks.Presentation.Errors;
using Backroom.BuildingBlocks.Presentation.Organizations;
using Backroom.Modules.Notifications.Application.Bell;
using Backroom.Modules.Notifications.Application.Settings;

namespace Backroom.Modules.Notifications.Presentation;

public static class NotificationsEndpoints
{
    /// <summary>The feature flag that turns on the notification endpoints (docs/contracts/phase-2.md, "Decisions", 27).</summary>
    public const string Flag = "Features:Notifications";

    private const long MaxSettingsRequestBytes = 1024;

    public static IEndpointRouteBuilder MapNotificationsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // Own entries only: every statement names the signed-in account, so there is no id to guess.
        endpoints.MapGet("/api/v1/orgs/{orgId:guid}/notifications", ListAsync).InOrganization();
        endpoints.MapPost("/api/v1/orgs/{orgId:guid}/notifications/read-all", ReadAllAsync).InOrganization();
        endpoints.MapPut("/api/v1/orgs/{orgId:guid}/notifications/{notificationId:guid}/read", ReadAsync).InOrganization();
        endpoints.MapGet("/api/v1/orgs/{orgId:guid}/notification-settings", GetSettingsAsync).InOrganization();
        endpoints.MapPut("/api/v1/orgs/{orgId:guid}/notification-settings", PutSettingsAsync)
            .InOrganization()
            .WithMetadata(new RequestSizeLimitAttribute(MaxSettingsRequestBytes));
        endpoints.MapGet("/api/v1/orgs/{orgId:guid}/conversations/{conversationId:guid}/notification-settings", GetConversationSettingsAsync).InOrganization();
        endpoints.MapPut("/api/v1/orgs/{orgId:guid}/conversations/{conversationId:guid}/notification-settings", PutConversationSettingsAsync)
            .InOrganization()
            .WithMetadata(new RequestSizeLimitAttribute(MaxSettingsRequestBytes));

        return endpoints;
    }

    private static async Task<IResult> ListAsync(string? tab, string? after, int? limit, ICurrentAccount account, BellService bell, CancellationToken cancellationToken)
    {
        var result = await bell.ListAsync(account.AccountId, tab, after, limit, cancellationToken);
        return result.ToHttpResult(page => TypedResults.Ok(new NotificationsResponse(
            [.. page.Entries.Select(entry => new NotificationResponse(
                entry.Item.Id, entry.Item.Kind, entry.Item.ConversationId, entry.Item.MessageId, entry.Item.ActorAccountId,
                entry.Item.CreatedAt, entry.Item.ReadAt, entry.Preview))],
            page.Next)));
    }

    private static async Task<IResult> ReadAllAsync(ICurrentAccount account, BellService bell, CancellationToken cancellationToken)
    {
        await bell.MarkAllReadAsync(account.AccountId, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> ReadAsync(Guid notificationId, ICurrentAccount account, BellService bell, CancellationToken cancellationToken)
    {
        var result = await bell.MarkReadAsync(account.AccountId, notificationId, cancellationToken);
        return result.IsSuccess ? TypedResults.NoContent() : result.ToHttpResult();
    }

    private static async Task<IResult> GetSettingsAsync(ICurrentAccount account, SettingsService settings, CancellationToken cancellationToken) =>
        TypedResults.Ok(ToResponse(await settings.GetAccountAsync(account.AccountId, cancellationToken)));

    private static async Task<IResult> PutSettingsAsync(AccountSettingsRequest request, ICurrentAccount account, SettingsService settings, CancellationToken cancellationToken)
    {
        var result = await settings.PutAccountAsync(account.AccountId, request.DefaultLevel, request.OrgMutedUntil, request.Sound, request.HidePreviews, cancellationToken);
        return result.ToHttpResult(saved => TypedResults.Ok(ToResponse(saved)));
    }

    private static async Task<IResult> GetConversationSettingsAsync(Guid conversationId, ICurrentAccount account, SettingsService settings, CancellationToken cancellationToken)
    {
        var result = await settings.GetConversationAsync(account.AccountId, conversationId, cancellationToken);
        return result.ToHttpResult(stored => TypedResults.Ok(stored is null
            ? new ConversationSettingsResponse(null, true, false, true)
            : ToResponse(stored)));
    }

    private static async Task<IResult> PutConversationSettingsAsync(
        Guid conversationId, ConversationSettingsRequest request, ICurrentAccount account, SettingsService settings, CancellationToken cancellationToken)
    {
        var result = await settings.PutConversationAsync(
            account.AccountId, conversationId, request.Level, request.FollowedThreadReplies, request.IgnoreGroupMentions, request.Sound, cancellationToken);
        return result.ToHttpResult(saved => TypedResults.Ok(ToResponse(saved)));
    }

    private static AccountSettingsResponse ToResponse(AccountSettings settings) =>
        new(NotificationLevelNames.Of(settings.DefaultLevel), settings.OrgMutedUntil, settings.Sound, settings.HidePreviews);

    private static ConversationSettingsResponse ToResponse(ConversationSettings settings) =>
        new(NotificationLevelNames.Of(settings.Level), settings.FollowedThreadReplies, settings.IgnoreGroupMentions, settings.Sound);
}
