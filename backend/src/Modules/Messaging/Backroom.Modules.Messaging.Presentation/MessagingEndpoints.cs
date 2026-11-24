using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Backroom.BuildingBlocks.Application.Accounts;
using Backroom.BuildingBlocks.Presentation.Errors;
using Backroom.BuildingBlocks.Presentation.Organizations;
using Backroom.Modules.Messaging.Application.Channels;
using Backroom.Modules.Messaging.Application.Conversations;
using Backroom.Modules.Messaging.Application.Messages;
using Backroom.Modules.Permissions.Contracts;

namespace Backroom.Modules.Messaging.Presentation;

public static class MessagingEndpoints
{
    /// <summary>
    /// The Phase 2 conversations flag (docs/contracts/phase-2.md, decision 27): the conversation routes that are new in Phase 2 (join and leave
    /// so far; listing conversations and their messages replace the Phase 1 channel routes and are always on). Off by
    /// default; the AppHost turns it on in development.
    /// </summary>
    public const string ConversationsFlag = "Features:Conversations";

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
        endpoints.MapGet("/api/v1/orgs/{orgId:guid}/conversations", ListConversationsAsync).InOrganization();
        endpoints.MapGet("/api/v1/orgs/{orgId:guid}/conversations/{conversationId:guid}/messages", ListMessagesAsync).InOrganization();
        endpoints.MapPost("/api/v1/orgs/{orgId:guid}/conversations/{conversationId:guid}/messages", SendMessageAsync)
            .PublishLiveEventsAfterCommit()
            .InOrganization()
            .WithMetadata(new RequestSizeLimitAttribute(MaxSendMessageRequestBytes));

        if (endpoints.ServiceProvider.GetRequiredService<IConfiguration>().GetValue<bool>(ConversationsFlag))
        {
            endpoints.MapPost("/api/v1/orgs/{orgId:guid}/conversations/{conversationId:guid}/join", JoinAsync).InOrganization();
            endpoints.MapPost("/api/v1/orgs/{orgId:guid}/conversations/{conversationId:guid}/leave", LeaveAsync).InOrganization();
        }

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

    private static async Task<IResult> ListConversationsAsync(Guid? after, int? limit, ICurrentAccount account, ConversationService conversations, CancellationToken cancellationToken)
    {
        var result = await conversations.ListAsync(account.AccountId, after, limit, cancellationToken);
        return result.ToHttpResult(page => TypedResults.Ok(new ConversationsResponse(
            [.. page.Items.Select(item => new ConversationResponse(item.Id, item.Kind, item.Name, item.CreatedAt))], page.Next)));
    }

    private static async Task<IResult> ListMessagesAsync(
        Guid conversationId, long? before, long? after, int? limit, ICurrentAccount account, MessageService messages, CancellationToken cancellationToken)
    {
        var result = await messages.ListAsync(account.AccountId, conversationId, before, after, limit, cancellationToken);
        return result.ToHttpResult(page => TypedResults.Ok(new MessagesResponse([.. page.Messages.Select(ToResponse)], page.Next)));
    }

    private static async Task<IResult> SendMessageAsync(Guid conversationId, SendMessageRequest request, ICurrentAccount account, MessageService messages, CancellationToken cancellationToken)
    {
        var result = await messages.SendAsync(account.AccountId, conversationId, request.ClientMessageId, request.Text, cancellationToken);
        return result.ToHttpResult(sent => sent.Created
            ? TypedResults.Created((string?)null, ToResponse(sent.Message))
            : TypedResults.Ok(ToResponse(sent.Message)));
    }

    private static async Task<IResult> JoinAsync(Guid conversationId, ICurrentAccount account, MembershipService memberships, CancellationToken cancellationToken) =>
        (await memberships.JoinAsync(account.AccountId, conversationId, cancellationToken)).ToHttpResult();

    private static async Task<IResult> LeaveAsync(Guid conversationId, ICurrentAccount account, MembershipService memberships, CancellationToken cancellationToken) =>
        (await memberships.LeaveAsync(account.AccountId, conversationId, cancellationToken)).ToHttpResult();

    private static ChannelResponse ToResponse(ChannelEntry channel) => new(channel.Id, channel.Name, channel.CreatedAt);

    private static MessageResponse ToResponse(MessageEntry message) =>
        new(message.Id, message.Seq, message.ClientMessageId, message.AuthorAccountId, message.Text, message.CreatedAt);
}
