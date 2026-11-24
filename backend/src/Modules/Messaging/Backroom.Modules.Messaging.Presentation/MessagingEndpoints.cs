using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Backroom.BuildingBlocks.Application.Accounts;
using Backroom.BuildingBlocks.Presentation.Errors;
using Backroom.BuildingBlocks.Presentation.Organizations;
using Backroom.Modules.Messaging.Application.Channels;
using Backroom.Modules.Messaging.Application.Messages;
using Backroom.Modules.Permissions.Contracts;

namespace Backroom.Modules.Messaging.Presentation;

public static class MessagingEndpoints
{
    /// <summary>A channel name: 80 characters, a few bytes each, plus the JSON around it.</summary>
    private const long MaxCreateChannelRequestBytes = 4 * 1024;

    /// <summary>4000 characters of text, up to four bytes each in UTF-8 (more when the JSON escapes them), plus the JSON around it.</summary>
    private const long MaxSendMessageRequestBytes = 64 * 1024;

    public static IEndpointRouteBuilder MapMessagingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/api/v1/orgs/{orgId:guid}/channels", ListChannelsAsync).InOrganization();
        endpoints.MapPost("/api/v1/orgs/{orgId:guid}/channels", CreateChannelAsync)
            .PublishLiveEventsAfterCommit()
            .InOrganization()
            .RequirePermission(SystemRoles.ChannelsCreatePublic)
            .WithMetadata(new RequestSizeLimitAttribute(MaxCreateChannelRequestBytes));
        endpoints.MapGet("/api/v1/orgs/{orgId:guid}/channels/{channelId:guid}/messages", ListMessagesAsync).InOrganization();
        endpoints.MapPost("/api/v1/orgs/{orgId:guid}/channels/{channelId:guid}/messages", SendMessageAsync)
            .PublishLiveEventsAfterCommit()
            .InOrganization()
            .WithMetadata(new RequestSizeLimitAttribute(MaxSendMessageRequestBytes));

        return endpoints;
    }

    private static async Task<IResult> ListChannelsAsync(Guid? after, int? limit, ChannelService channels, CancellationToken cancellationToken)
    {
        var result = await channels.ListAsync(after, limit, cancellationToken);
        return result.ToHttpResult(page => TypedResults.Ok(new ChannelsResponse([.. page.Channels.Select(ToResponse)], page.Next)));
    }

    private static async Task<IResult> CreateChannelAsync(CreateChannelRequest request, ICurrentAccount account, ChannelService channels, CancellationToken cancellationToken)
    {
        var result = await channels.CreateAsync(account.AccountId, request.Name, cancellationToken);
        return result.ToHttpResult(channel => TypedResults.Created((string?)null, ToResponse(channel)));
    }

    private static async Task<IResult> ListMessagesAsync(Guid channelId, Guid? before, Guid? after, int? limit, MessageService messages, CancellationToken cancellationToken)
    {
        var result = await messages.ListAsync(channelId, before, after, limit, cancellationToken);
        return result.ToHttpResult(page => TypedResults.Ok(new MessagesResponse([.. page.Messages.Select(ToResponse)], page.Next)));
    }

    private static async Task<IResult> SendMessageAsync(Guid channelId, SendMessageRequest request, ICurrentAccount account, MessageService messages, CancellationToken cancellationToken)
    {
        var result = await messages.SendAsync(account.AccountId, channelId, request.ClientMessageId, request.Text, cancellationToken);
        return result.ToHttpResult(sent => sent.Created
            ? TypedResults.Created((string?)null, ToResponse(sent.Message))
            : TypedResults.Ok(ToResponse(sent.Message)));
    }

    private static ChannelResponse ToResponse(ChannelEntry channel) => new(channel.Id, channel.Name, channel.CreatedAt);

    private static MessageResponse ToResponse(MessageEntry message) =>
        new(message.Id, message.ClientMessageId, message.AuthorAccountId, message.Text, message.CreatedAt);
}
