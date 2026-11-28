using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
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
    // Room for the 50 initial members.
    private const long MaxCreateChannelRequestBytes = 8 * 1024;

    private const long MaxManagementRequestBytes = 8 * 1024;

    /// <summary>32 KB of blocks (MessageBlocks.MaxJsonBytes), or 4000 characters of text at up to four bytes each in UTF-8 (more when the JSON escapes them), plus the JSON around them.</summary>
    private const long MaxSendMessageRequestBytes = 64 * 1024;

    public static IEndpointRouteBuilder MapMessagingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/api/v1/orgs/{orgId:guid}/channels", ListChannelsAsync).InOrganization();
        // The permission depends on the kind (channels.create-public or channels.create-private): the service checks it.
        endpoints.MapPost("/api/v1/orgs/{orgId:guid}/channels", CreateChannelAsync)
            .PublishLiveEventsAfterCommit()
            .InOrganization()
            .WithMetadata(new RequestSizeLimitAttribute(MaxCreateChannelRequestBytes));
        endpoints.MapGet("/api/v1/orgs/{orgId:guid}/conversations", ListConversationsAsync).InOrganization();
        endpoints.MapGet("/api/v1/orgs/{orgId:guid}/conversations/{conversationId:guid}/messages", ListMessagesAsync).InOrganization();
        endpoints.MapPost("/api/v1/orgs/{orgId:guid}/conversations/{conversationId:guid}/messages", SendMessageAsync)
            .PublishLiveEventsAfterCommit()
            .InOrganization()
            .WithMetadata(new RequestSizeLimitAttribute(MaxSendMessageRequestBytes));
        // Edit and delete: a limit per account and organization (30 a minute each), checked before the request is read.
        endpoints.MapPatch("/api/v1/orgs/{orgId:guid}/messages/{messageId:guid}", EditMessageAsync)
            .PublishLiveEventsAfterCommit()
            .InOrganization()
            .RequireRateLimiting(MessageEditRateLimitPolicy.Name)
            .WithMetadata(new RequestSizeLimitAttribute(MaxSendMessageRequestBytes));
        endpoints.MapDelete("/api/v1/orgs/{orgId:guid}/messages/{messageId:guid}", DeleteMessageAsync)
            .PublishLiveEventsAfterCommit()
            .InOrganization()
            .RequireRateLimiting(MessageDeleteRateLimitPolicy.Name);
        endpoints.MapPost("/api/v1/orgs/{orgId:guid}/conversations/{conversationId:guid}/join", JoinAsync)
            .PublishLiveEventsAfterCommit()
            .InOrganization();
        endpoints.MapPost("/api/v1/orgs/{orgId:guid}/conversations/{conversationId:guid}/leave", LeaveAsync)
            .PublishLiveEventsAfterCommit()
            .InOrganization();


        // Admins see how many private channels exist and nothing else about them (owner decision 11).
        endpoints.MapGet("/api/v1/orgs/{orgId:guid}/private-channels/count", CountPrivateChannelsAsync)
            .InOrganization()
            .RequirePermission(SystemRoles.ChannelsManagePublic);
        endpoints.MapGet("/api/v1/orgs/{orgId:guid}/conversations/{conversationId:guid}", GetConversationAsync).InOrganization();
        endpoints.MapPatch("/api/v1/orgs/{orgId:guid}/conversations/{conversationId:guid}", UpdateConversationAsync)
            .PublishLiveEventsAfterCommit()
            .InOrganization()
            .WithMetadata(new RequestSizeLimitAttribute(MaxManagementRequestBytes));
        endpoints.MapPost("/api/v1/orgs/{orgId:guid}/conversations/{conversationId:guid}/archive", ArchiveAsync)
            .PublishLiveEventsAfterCommit()
            .InOrganization();
        endpoints.MapPost("/api/v1/orgs/{orgId:guid}/conversations/{conversationId:guid}/unarchive", UnarchiveAsync)
            .PublishLiveEventsAfterCommit()
            .InOrganization();
        endpoints.MapGet("/api/v1/orgs/{orgId:guid}/conversations/{conversationId:guid}/members", ListMembersAsync).InOrganization();
        endpoints.MapPost("/api/v1/orgs/{orgId:guid}/conversations/{conversationId:guid}/members", AddMembersAsync)
            .PublishLiveEventsAfterCommit()
            .InOrganization()
            .WithMetadata(new RequestSizeLimitAttribute(MaxManagementRequestBytes));
        endpoints.MapDelete("/api/v1/orgs/{orgId:guid}/conversations/{conversationId:guid}/members/{accountId:guid}", RemoveMemberAsync)
            .PublishLiveEventsAfterCommit()
            .InOrganization();
        endpoints.MapPut("/api/v1/orgs/{orgId:guid}/conversations/{conversationId:guid}/owner", TransferOwnershipAsync)
            .PublishLiveEventsAfterCommit()
            .InOrganization()
            .WithMetadata(new RequestSizeLimitAttribute(MaxManagementRequestBytes));

        return endpoints;
    }

    /// <summary>The rate limits of edit and delete; the host also calls <c>UseRateLimiter</c>.</summary>
    public static IServiceCollection AddMessagingEndpoints(this IServiceCollection services) =>
        services.AddRateLimiter(options =>
        {
            options.AddPolicy<string, MessageEditRateLimitPolicy>(MessageEditRateLimitPolicy.Name);
            options.AddPolicy<string, MessageDeleteRateLimitPolicy>(MessageDeleteRateLimitPolicy.Name);
        });

    private static async Task<IResult> EditMessageAsync(
        Guid messageId, EditMessageRequest request, ICurrentAccount account, MessageService messages, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var result = await messages.EditAsync(account.AccountId, messageId, request.Blocks, cancellationToken);
        return IsEpochUnconfirmed(result)
            ? EpochUnconfirmed(httpContext)
            : result.ToHttpResult(edited => TypedResults.Ok(ToResponse(edited.Message, edited.Epoch)));
    }

    private static async Task<IResult> DeleteMessageAsync(
        Guid messageId, ICurrentAccount account, MessageService messages, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var result = await messages.DeleteAsync(account.AccountId, messageId, cancellationToken);
        return result.Error == MessageService.EpochUnconfirmed ? EpochUnconfirmed(httpContext) : result.ToHttpResult();
    }

    private static async Task<IResult> ListChannelsAsync(Guid? after, int? limit, ChannelService channels, CancellationToken cancellationToken)
    {
        var result = await channels.ListAsync(after, limit, cancellationToken);
        return result.ToHttpResult(page => TypedResults.Ok(new ChannelsResponse([.. page.Channels.Select(ToResponse)], page.Next)));
    }

    private static async Task<IResult> CreateChannelAsync(CreateChannelRequest request, ICurrentAccount account, ChannelService channels, CancellationToken cancellationToken)
    {
        var result = await channels.CreateAsync(
            account.AccountId, request.Name, request.Private ?? false, request.Description, request.MemberAccountIds, cancellationToken);
        return result.ToHttpResult(channel => TypedResults.Created((string?)null, ToResponse(channel)));
    }

    private static async Task<IResult> ListConversationsAsync(bool? archived, string? after, int? limit, ICurrentAccount account, ConversationService conversations, CancellationToken cancellationToken)
    {
        var result = await conversations.ListAsync(account.AccountId, archived ?? false, after, limit, cancellationToken);
        return result.ToHttpResult(page => TypedResults.Ok(new ConversationsResponse(
            [.. page.Items.Select(item => new ConversationResponse(item.Id, item.Kind, item.Name, item.CreatedAt))], page.Next)));
    }

    private static async Task<IResult> ListMessagesAsync(
        Guid conversationId,
        string? epoch,
        long? before,
        long? after,
        [FromQuery(Name = "changed_after")] long? changedAfter,
        Guid? around,
        int? limit,
        ICurrentAccount account,
        MessageService messages,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (around is { } anchorId)
        {
            var aroundResult = await messages.ListAroundAsync(account.AccountId, conversationId, anchorId, epoch, before, after, changedAfter, limit, cancellationToken);
            return IsEpochUnconfirmed(aroundResult)
                ? EpochUnconfirmed(httpContext)
                : aroundResult.ToHttpResult(page => TypedResults.Ok(new MessagesAroundResponse(
                    [.. page.Messages.Select(ToResponse)], page.Before, page.After, new MessageAnchorResponse(page.Anchor.Id, page.Anchor.Seq), page.Epoch)));
        }

        var result = await messages.ListAsync(account.AccountId, conversationId, epoch, before, after, changedAfter, limit, cancellationToken);
        return IsEpochUnconfirmed(result)
            ? EpochUnconfirmed(httpContext)
            : result.ToHttpResult(page => TypedResults.Ok(new MessagesResponse([.. page.Messages.Select(ToResponse)], page.Next, page.Epoch)));
    }

    private static async Task<IResult> SendMessageAsync(
        Guid conversationId, SendMessageRequest request, ICurrentAccount account, MessageService messages, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var result = await messages.SendAsync(account.AccountId, conversationId, request.ClientMessageId, request.Text, request.Blocks, request.ReplyToMessageId, cancellationToken);
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

    private static async Task<IResult> JoinAsync(
        Guid conversationId, ICurrentAccount account, MembershipService memberships, HttpContext httpContext, CancellationToken cancellationToken) =>
        ToHttpResult(await memberships.JoinAsync(account.AccountId, conversationId, cancellationToken), httpContext);

    private static async Task<IResult> LeaveAsync(
        Guid conversationId, ICurrentAccount account, MembershipService memberships, HttpContext httpContext, CancellationToken cancellationToken) =>
        ToHttpResult(await memberships.LeaveAsync(account.AccountId, conversationId, cancellationToken), httpContext);

    // Changes that take a change number answer 503 while the cell has no confirmed epoch.
    private static IResult ToHttpResult(Result result, HttpContext httpContext) =>
        result.Error == MessageService.EpochUnconfirmed ? EpochUnconfirmed(httpContext) : result.ToHttpResult();

    private static ChannelResponse ToResponse(ChannelEntry channel) => new(channel.Id, channel.Kind, channel.Name, channel.Description, channel.CreatedAt);

    private static async Task<IResult> CountPrivateChannelsAsync(ConversationManagementService conversations, CancellationToken cancellationToken) =>
        TypedResults.Ok(new PrivateChannelCountResponse(await conversations.CountPrivateChannelsAsync(cancellationToken)));

    private static async Task<IResult> GetConversationAsync(
        Guid conversationId, ICurrentAccount account, ConversationManagementService conversations, CancellationToken cancellationToken) =>
        (await conversations.GetAsync(account.AccountId, conversationId, cancellationToken)).ToHttpResult(conversation => TypedResults.Ok(ToResponse(conversation)));

    private static async Task<IResult> UpdateConversationAsync(
        Guid conversationId, UpdateConversationRequest request, ICurrentAccount account, ConversationManagementService conversations, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var result = await conversations.UpdateAsync(account.AccountId, conversationId, request.Name, request.Description, cancellationToken);
        return IsEpochUnconfirmed(result)
            ? EpochUnconfirmed(httpContext)
            : result.ToHttpResult(conversation => TypedResults.Ok(ToResponse(conversation)));
    }

    private static async Task<IResult> ArchiveAsync(
        Guid conversationId, ICurrentAccount account, ConversationManagementService conversations, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var result = await conversations.ArchiveAsync(account.AccountId, conversationId, cancellationToken);
        return ToHttpResult(result, httpContext);
    }

    private static async Task<IResult> UnarchiveAsync(
        Guid conversationId, ICurrentAccount account, ConversationManagementService conversations, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var result = await conversations.UnarchiveAsync(account.AccountId, conversationId, cancellationToken);
        return ToHttpResult(result, httpContext);
    }

    private static async Task<IResult> ListMembersAsync(
        Guid conversationId, Guid? after, int? limit, ICurrentAccount account, MembershipService memberships, CancellationToken cancellationToken)
    {
        var result = await memberships.ListAsync(account.AccountId, conversationId, after, limit, cancellationToken);
        return result.ToHttpResult(page => TypedResults.Ok(new ConversationMembersResponse(
            [.. page.Items.Select(member => new ConversationMemberResponse(member.AccountId, member.Role, member.JoinedAt))], page.Next?.ToString("D"))));
    }

    private static async Task<IResult> AddMembersAsync(
        Guid conversationId, AddMembersRequest request, ICurrentAccount account, MembershipService memberships, HttpContext httpContext, CancellationToken cancellationToken) =>
        ToHttpResult(await memberships.AddAsync(account.AccountId, conversationId, request.AccountIds, cancellationToken), httpContext);

    private static async Task<IResult> RemoveMemberAsync(
        Guid conversationId, Guid accountId, ICurrentAccount account, MembershipService memberships, HttpContext httpContext, CancellationToken cancellationToken) =>
        ToHttpResult(await memberships.RemoveAsync(account.AccountId, conversationId, accountId, cancellationToken), httpContext);

    private static async Task<IResult> TransferOwnershipAsync(
        Guid conversationId, TransferOwnershipRequest request, ICurrentAccount account, MembershipService memberships, HttpContext httpContext, CancellationToken cancellationToken) =>
        ToHttpResult(await memberships.TransferOwnershipAsync(account.AccountId, conversationId, request.AccountId, cancellationToken), httpContext);

    private static ConversationDetailResponse ToResponse(ConversationDetails conversation) => new(
        conversation.Id, conversation.Kind, conversation.Name, conversation.Description, conversation.CreatedAt, conversation.ArchivedAt,
        conversation.MemberCount, conversation.OwnerAccountId, conversation.Role);

    private static MessageQuoteResponse? ToQuote(MessageQuote? quote) =>
        quote is null ? null : new(quote.MessageId, quote.AuthorAccountId, quote.Text, quote.Deleted);

    // A tombstone answers with its numbers and times only (docs/adr/0011): no author, no client id, no content.
    private static MessageResponse ToResponse(MessageEntry message) => message.DeletedAt is null
        ? new(message.Id, message.Seq, message.VersionSeq, message.ClientMessageId, message.AuthorAccountId, message.Text, message.Blocks, message.CreatedAt, null, message.EditedAt, ToQuote(message.ReplyTo))
        : new(message.Id, message.Seq, message.VersionSeq, null, null, null, null, message.CreatedAt, message.DeletedAt, null);

    private static SentMessageResponse ToResponse(MessageEntry message, string epoch) => message.DeletedAt is null
        ? new(message.Id, message.Seq, message.VersionSeq, message.ClientMessageId, message.AuthorAccountId, message.Text, message.Blocks, message.CreatedAt, null, message.EditedAt, epoch, ToQuote(message.ReplyTo))
        : new(message.Id, message.Seq, message.VersionSeq, null, null, null, null, message.CreatedAt, message.DeletedAt, null, epoch);
}
