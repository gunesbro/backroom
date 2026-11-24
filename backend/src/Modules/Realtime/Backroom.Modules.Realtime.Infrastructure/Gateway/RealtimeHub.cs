using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Backroom.BuildingBlocks.Application.Organizations;
using Backroom.BuildingBlocks.Application.Persistence;
using Backroom.Modules.Messaging.Contracts;
using Backroom.Modules.Realtime.Infrastructure.Revocation;

namespace Backroom.Modules.Realtime.Infrastructure.Gateway;

/// <summary>
/// The hub at <see cref="RealtimeEndpoints.HubPath"/> (docs/contracts/phase-1.md, "Live events"). Clients only subscribe
/// and unsubscribe; the server sends everything else (<see cref="Contracts.ILiveEventPublisher"/>). A subscription is
/// granted after the same checks as a read: an active member of the organization and, for a channel, one that exists, is
/// public and is not archived. Every refusal for a missing organization, membership or channel answers the same
/// <c>realtime.not_found</c>, so a client learns nothing about what it may not see. Errors are <see cref="HubException"/>s
/// with a stable code and no internals; anything else the client gets is SignalR's generic failure.
/// </summary>
[Authorize]
internal sealed partial class RealtimeHub(
    ConnectionRegistry registry,
    RevocationChecker revocations,
    TimeProvider timeProvider,
    IOrganizationMembershipCheck membership,
    IChannelReadAccess channels,
    OrganizationContext organization,
    IUnitOfWork unitOfWork,
    IOptions<RealtimeOptions> options,
    ILogger<RealtimeHub> logger) : Hub
{
    public const string NotFound = "realtime.not_found";
    public const string InvalidArgument = "realtime.invalid_argument";
    public const string SubscriptionLimit = "realtime.subscription_limit";
    public const string Unavailable = "realtime.unavailable";
    public const string RateLimited = "realtime.rate_limited";
    public const string TooManyRefusals = "realtime.too_many_refusals";

    public override async Task OnConnectedAsync()
    {
        if (!AccountUserIdProvider.TryGetAccountId(Context.User, out var accountId))
        {
            Context.Abort();
            return;
        }

        if (registry.TryAdd(Context.ConnectionId, accountId, options.Value.MaxConnectionsPerAccount) is not { } state)
        {
            RealtimeTelemetry.RejectedSubscriptions.Add(1, new KeyValuePair<string, object?>("reason", "connection_limit"));
            LogConnectionRefused(logger, accountId);
            Context.Abort();
            return;
        }

        RealtimeTelemetry.Connections.Add(1);

        // What a revocation needs to find this connection. A revocation that arrived between the request's check and the
        // registration is caught here (answered from the cache the check just filled; an unreadable store was refused already).
        if (TokenIdentity.TryRead(Context.User, out var identity))
        {
            state.Bind(identity.SessionId, identity.IssuedAt, Context.Abort);
            if (await revocations.CheckAsync(identity.SessionId, identity.AccountId, identity.IssuedAt, Context.ConnectionAborted) == RevocationVerdict.Revoked)
            {
                Context.Abort();
            }
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (registry.Remove(Context.ConnectionId) is not { } state)
        {
            return;
        }

        RealtimeTelemetry.Connections.Add(-1);
        // SignalR drops the connection from its groups itself; the state only has to stop counting.
        await state.Gate.WaitAsync();
        try
        {
            RealtimeTelemetry.Subscriptions.Add(-state.Count);
            state.Closed = true;
        }
        finally
        {
            state.Gate.Release();
        }
    }

    public Task SubscribeOrganization(Guid orgId) =>
        SubscribeAsync(orgId, channelId: null);

    public Task UnsubscribeOrganization(Guid orgId)
    {
        Require(orgId);
        return UnsubscribeAsync(GroupNames.Organization(options.Value.CellId, orgId));
    }

    public Task SubscribeChannel(Guid orgId, Guid channelId)
    {
        ValidateIds(orgId, channelId);
        return SubscribeAsync(orgId, channelId);
    }

    public Task UnsubscribeChannel(Guid orgId, Guid channelId)
    {
        ValidateIds(orgId, channelId);
        return UnsubscribeAsync(GroupNames.Channel(options.Value.CellId, orgId, channelId));
    }

    private static void Require(Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new HubException(InvalidArgument);
        }
    }

    private static void ValidateIds(Guid organizationId, Guid channelId)
    {
        if (organizationId == Guid.Empty || channelId == Guid.Empty)
        {
            throw new HubException(InvalidArgument);
        }
    }

    private async Task SubscribeAsync(Guid organizationId, Guid? channelId)
    {
        if (channelId is null)
        {
            Require(organizationId);
        }

        var state = Current();
        var group = channelId is { } channel
            ? GroupNames.Channel(options.Value.CellId, organizationId, channel)
            : GroupNames.Organization(options.Value.CellId, organizationId);
        var cancellationToken = Context.ConnectionAborted;

        // The gate is held from the check to the join, so a removal that follows a membership's end cannot slip between them.
        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            if (state.Closed)
            {
                throw new HubException(Unavailable);
            }

            // Every subscribe call counts, a repeat too: a client that keeps asking is cut off.
            if (!state.TryCountCall(timeProvider.GetUtcNow(), options.Value.SubscribeWindow, options.Value.MaxSubscribeCallsPerWindow))
            {
                RealtimeTelemetry.RejectedSubscriptions.Add(1, new KeyValuePair<string, object?>("reason", "rate_limited"));
                Context.Abort();
                throw new HubException(RateLimited);
            }

            if (state.Contains(group))
            {
                return;
            }

            if (state.Count >= options.Value.MaxSubscriptionsPerConnection)
            {
                RealtimeTelemetry.RejectedSubscriptions.Add(1, new KeyValuePair<string, object?>("reason", "subscription_limit"));
                throw new HubException(SubscriptionLimit);
            }

            if (!await MayJoinAsync(state.AccountId, organizationId, channelId, cancellationToken))
            {
                RealtimeTelemetry.RejectedSubscriptions.Add(1, new KeyValuePair<string, object?>("reason", "not_found"));
                if (state.CountRefusal() >= options.Value.MaxRefusedSubscribesPerConnection)
                {
                    Context.Abort();
                    throw new HubException(TooManyRefusals);
                }

                throw new HubException(NotFound);
            }

            state.Add(group, organizationId);
            try
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, group, cancellationToken);
            }
            catch
            {
                state.Remove(group);
                throw;
            }

            RealtimeTelemetry.Subscriptions.Add(1);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private async Task UnsubscribeAsync(string group)
    {
        var state = Current();
        await state.Gate.WaitAsync(Context.ConnectionAborted);
        try
        {
            if (state.Remove(group))
            {
                RealtimeTelemetry.Subscriptions.Add(-1);
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, group, Context.ConnectionAborted);
            }
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private ConnectionState Current() =>
        registry.Find(Context.ConnectionId) ?? throw new HubException(Unavailable);

    // The organization comes from the argument, and is accepted only after the membership check, like a request's route
    // value; row-level security limits everything the transaction reads to it.
    private async Task<bool> MayJoinAsync(Guid accountId, Guid organizationId, Guid? channelId, CancellationToken cancellationToken)
    {
        try
        {
            organization.Enter(organizationId);
            await unitOfWork.BeginAsync(cancellationToken);
            var allowed = await membership.IsActiveMemberAsync(accountId, cancellationToken)
                && (channelId is not { } channel || await channels.CanReadAsync(organizationId, channel, accountId, cancellationToken));
            await unitOfWork.CommitAsync(cancellationToken);
            return allowed;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // The client gets a stable code; the cause (database down, bad state) goes to the log.
        catch (Exception exception) when (exception is not HubException)
#pragma warning restore CA1031
        {
            LogCheckFailed(logger, exception.GetType().Name, organizationId);
            throw new HubException(Unavailable);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "A hub connection of account {AccountId} was refused: too many connections.")]
    private static partial void LogConnectionRefused(ILogger logger, Guid accountId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "The subscription check failed ({ExceptionType}) in organization {OrganizationId}.")]
    private static partial void LogCheckFailed(ILogger logger, string exceptionType, Guid organizationId);
}
