using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Backroom.BuildingBlocks.Application.Accounts;
using Backroom.BuildingBlocks.Domain.Errors;
using Backroom.BuildingBlocks.Presentation.Errors;
using Backroom.BuildingBlocks.Presentation.Organizations;
using Backroom.Modules.Messaging.Application.Channels;
using Backroom.Modules.Messaging.Application.Conversations;
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
        endpoints.MapGet("/api/v1/orgs/{orgId:guid}/conversations", ListConversationsAsync).InOrganization();
        endpoints.MapGet("/api/v1/orgs/{orgId:guid}/conversations/{conversationId:guid}/messages", ListMessagesAsync).InOrganization();
        endpoints.MapPost("/api/v1/orgs/{orgId:guid}/conversations/{conversationId:guid}/messages", SendMessageAsync)
            .PublishLiveEventsAfterCommit()
            .InOrganization()
            .WithMetadata(new RequestSizeLimitAttribute(MaxSendMessageRequestBytes));
        endpoints.MapPost("/api/v1/orgs/{orgId:guid}/conversations/{conversationId:guid}/join", JoinAsync).InOrganization();
        endpoints.MapPost("/api/v1/orgs/{orgId:guid}/conversations/{conversationId:guid}/leave", LeaveAsync)
            .PublishLiveEventsAfterCommit()
            .InOrganization();

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

    private static async Task<IResult> ListConversationsAsync(string? after, int? limit, ICurrentAccount account, ConversationService conversations, CancellationToken cancellationToken)
    {
        var result = await conversations.ListAsync(account.AccountId, after, limit, cancellationToken);
        return result.ToHttpResult(page => TypedResults.Ok(new ConversationsResponse(
            [.. page.Items.Select(item => new ConversationResponse(item.Id, item.Kind, item.Name, item.CreatedAt))], page.Next)));
    }

    private static async Task<IResult> ListMessagesAsync(
        Guid conversationId,
        string? epoch,
        long? before,
        long? after,
        [FromQuery(Name = "changed_after")] long? changedAfter,
        int? limit,
        ICurrentAccount account,
        MessageService messages,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var result = await messages.ListAsync(account.AccountId, conversationId, epoch, before, after, changedAfter, limit, cancellationToken);
        return IsEpochUnconfirmed(result)
            ? EpochUnconfirmed(httpContext)
            : result.ToHttpResult(page => TypedResults.Ok(new MessagesResponse([.. page.Messages.Select(ToResponse)], page.Next, page.Epoch)));
    }

    private static async Task<IResult> SendMessageAsync(
        Guid conversationId, SendMessageRequest request, ICurrentAccount account, MessageService messages, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var result = await messages.SendAsync(account.AccountId, conversationId, request.ClientMessageId, request.Text, cancellationToken);
        return IsEpochUnconfirmed(result)
            ? EpochUnconfirmed(httpContext)
            : result.ToHttpResult(sent => sent.Created
                ? TypedResults.Created((string?)null, ToResponse(sent.Message, sent.Epoch))
                : TypedResults.Ok(ToResponse(sent.Message, sent.Epoch)));
    }

    private static bool IsEpochUnconfirmed<T>(Result<T> result) => !result.IsSuccess && result.Error == MessageService.EpochUnconfirmed;

    /// <summary>
    /// No number can be handed out while the cell has no confirmed epoch (docs/adr/0011, "Cell epoch"): the same answer
    /// as a fenced write (<c>Retry-After</c>, never cached), so clients retry after the worker has reconnected.
    /// </summary>
    private static ProblemHttpResult EpochUnconfirmed(HttpContext httpContext)
    {
        httpContext.Response.Headers.RetryAfter = "5";
        httpContext.Response.Headers.CacheControl = "no-store";
        return TypedResults.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            detail: MessageService.EpochUnconfirmed.Description,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { [ErrorResults.CodeExtension] = MessageService.EpochUnconfirmed.Code });
    }

    private static async Task<IResult> JoinAsync(Guid conversationId, ICurrentAccount account, MembershipService memberships, CancellationToken cancellationToken) =>
        (await memberships.JoinAsync(account.AccountId, conversationId, cancellationToken)).ToHttpResult();

    private static async Task<IResult> LeaveAsync(Guid conversationId, ICurrentAccount account, MembershipService memberships, CancellationToken cancellationToken) =>
        (await memberships.LeaveAsync(account.AccountId, conversationId, cancellationToken)).ToHttpResult();

    private static ChannelResponse ToResponse(ChannelEntry channel) => new(channel.Id, channel.Name, channel.CreatedAt);

    // A tombstone answers with its numbers and times only (docs/adr/0011): no author, no client id, no content.
    private static MessageResponse ToResponse(MessageEntry message) => message.DeletedAt is null
        ? new(message.Id, message.Seq, message.VersionSeq, message.ClientMessageId, message.AuthorAccountId, message.Text, message.CreatedAt, null)
        : new(message.Id, message.Seq, message.VersionSeq, null, null, null, message.CreatedAt, message.DeletedAt);

    private static SentMessageResponse ToResponse(MessageEntry message, string epoch) => message.DeletedAt is null
        ? new(message.Id, message.Seq, message.VersionSeq, message.ClientMessageId, message.AuthorAccountId, message.Text, message.CreatedAt, null, epoch)
        : new(message.Id, message.Seq, message.VersionSeq, null, null, null, message.CreatedAt, message.DeletedAt, epoch);
}
