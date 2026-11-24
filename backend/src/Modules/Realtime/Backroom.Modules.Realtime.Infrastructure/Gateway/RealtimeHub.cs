using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Backroom.BuildingBlocks.Application.Organizations;
using Backroom.BuildingBlocks.Application.Persistence;
using Backroom.Modules.Messaging.Contracts;
using Backroom.Modules.Presence.Contracts;
using Backroom.Modules.Realtime.Infrastructure.Revocation;

namespace Backroom.Modules.Realtime.Infrastructure.Gateway;

/// <summary>
/// The hub at <see cref="RealtimeEndpoints.HubPath"/> (docs/contracts/phase-1.md, "Live events"). Clients only subscribe
/// and unsubscribe; the server sends everything else (<see cref="Contracts.ILiveEventPublisher"/>). A subscription is
/// granted after the same checks as a read: an active member of the organization and, for a channel, read access to it
/// (<c>IConversationReadAccess</c>: an open public channel, or an open conversation the account is a member of). Every
/// refusal for a missing organization, membership or channel answers the same <c>realtime.not_found</c>, so a client
/// learns nothing about what it may not see. Errors are <see cref="HubException"/>s
/// with a stable code and no internals; anything else the client gets is SignalR's generic failure.
/// </summary>
[Authorize]
internal sealed partial class RealtimeHub(
    ConnectionRegistry registry,
    RevocationChecker revocations,
    DelayedAbort aborts,
    TimeProvider timeProvider,
    IOrganizationMembershipCheck membership,
    IPresenceTracker presence,
    IConversationReadAccess conversations,
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

    /// <summary>This node tracks its limit of connections for presence; the client may try again later (or on another node).</summary>
    public const string PresenceUnavailable = "realtime.presence_unavailable";

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
                // The client finished its handshake before the node registered the connection, so the broadcast did not find it: tell it
                // here, as the broadcast would have, and close it after the grace (on a timer, so this call does not wait), so the notice
                // can leave first. Whatever happens to the notice, the connection is closed.
                try
                {
                    await Clients.Caller.SendAsync(Contracts.LiveEventNames.SessionRevoked, new Contracts.SessionRevokedPayload(Contracts.SessionRevokedPayload.SessionEnded), Context.ConnectionAborted);
                }
#pragma warning disable CA1031 // Any failure to send the notice (the client is gone, the pipe is closed) still ends in closing the connection.
                catch (Exception exception)
#pragma warning restore CA1031
                {
                    LogNoticeFailed(logger, exception.GetType().Name);
                }

                aborts.Schedule(Context.Abort);
                return;
            }
        }

        // Presence (docs/contracts/phase-2.md, flow 9): the connection makes the person online; never throws for Valkey.
        await presence.ConnectedAsync(Context.ConnectionId, accountId, Context.ConnectionAborted);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (registry.Remove(Context.ConnectionId) is not { } state)
        {
            return;
        }

        RealtimeTelemetry.Connections.Add(-1);
        await presence.DisconnectedAsync(Context.ConnectionId, CancellationToken.None);

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

    /// <summary>
    /// Replaces the connection's watch set (docs/contracts/phase-2.md, "Live events"): the connection joins the presence groups of
    /// <paramref name="accountIds"/> in the organization and leaves those it no longer watches. The caller must be an active member
    /// (else <see cref="NotFound"/>, as for a subscription); people who are not active members there are left out without a word, so the
    /// answer tells nothing about them. At most <see cref="RealtimeOptions.MaxWatchedPerConnection"/> ids; an empty list clears the set.
    /// </summary>
    public Task WatchPresence(Guid orgId, Guid[] accountIds) => WatchPresenceAsync(orgId, accountIds);

    private async Task WatchPresenceAsync(Guid orgId, Guid[] accountIds)
    {
        if (!presence.Enabled)
        {
            throw new HubException(NotFound);
        }

        if (!presence.IsTracking(Context.ConnectionId))
        {
            RealtimeTelemetry.RejectedSubscriptions.Add(1, new KeyValuePair<string, object?>("reason", "presence_unavailable"));
            throw new HubException(PresenceUnavailable);
        }

        Require(orgId);
        if (accountIds is null || accountIds.Length > options.Value.MaxWatchedPerConnection || accountIds.Contains(Guid.Empty))
        {
            throw new HubException(InvalidArgument);
        }

        var state = Current();
        var cancellationToken = Context.ConnectionAborted;
        IReadOnlyList<Guid> members;
        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            if (state.Closed)
            {
                throw new HubException(Unavailable);
            }

            // A watch counts as a subscribe call: one call joins up to 200 groups.
            if (!state.TryCountCall(timeProvider.GetUtcNow(), options.Value.SubscribeWindow, options.Value.MaxSubscribeCallsPerWindow))
            {
                RealtimeTelemetry.RejectedSubscriptions.Add(1, new KeyValuePair<string, object?>("reason", "rate_limited"));
                Context.Abort();
                throw new HubException(RateLimited);
            }

            if (await WatchableAsync(state.AccountId, orgId, [.. accountIds.Distinct()], cancellationToken) is not { } watchable)
            {
                RealtimeTelemetry.RejectedSubscriptions.Add(1, new KeyValuePair<string, object?>("reason", "not_found"));
                if (state.CountRefusal() >= options.Value.MaxRefusedSubscribesPerConnection)
                {
                    Context.Abort();
                    throw new HubException(TooManyRefusals);
                }

                throw new HubException(NotFound);
            }

            members = watchable;
            var cellId = options.Value.CellId;
            var wanted = members.Select(accountId => GroupNames.Presence(cellId, orgId, accountId)).ToHashSet(StringComparer.Ordinal);
            foreach (var group in state.Watched.Where(group => !wanted.Contains(group)).ToList())
            {
                state.Remove(group);
                RealtimeTelemetry.Subscriptions.Add(-1);
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, group, cancellationToken);
            }

            var joining = wanted.Where(group => !state.Contains(group)).ToList();
            if (state.Count + joining.Count > options.Value.MaxSubscriptionsPerConnection)
            {
                RealtimeTelemetry.RejectedSubscriptions.Add(1, new KeyValuePair<string, object?>("reason", "subscription_limit"));
                throw new HubException(SubscriptionLimit);
            }

            foreach (var group in joining)
            {
                state.AddWatched(group, orgId);
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
        }
        finally
        {
            state.Gate.Release();
        }

        // Their changes now reach this organization's presence groups (never throws for Valkey).
        await presence.WatchAsync(Context.ConnectionId, orgId, members, cancellationToken);
    }

    /// <summary>
    /// The client's report of input (<c>active</c>) or of 10 minutes without it (<c>idle</c>), at most one per
    /// <see cref="RealtimeOptions.ActivityInterval"/>; extra reports are dropped, so a client sends the latest state when it may.
    /// </summary>
    public Task ReportActivity(string state)
    {
        if (!presence.Enabled)
        {
            throw new HubException(NotFound);
        }

        var active = state switch
        {
            "active" => true,
            "idle" => false,
            _ => throw new HubException(InvalidArgument),
        };
        if (!Current().TryCountActivity(timeProvider.GetUtcNow(), options.Value.ActivityInterval))
        {
            RealtimeTelemetry.DroppedActivity.Add(1);
            return Task.CompletedTask;
        }

        return presence.ActivityAsync(Context.ConnectionId, active, Context.ConnectionAborted);
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
                && (channelId is not { } channel || await conversations.CanReadAsync(organizationId, channel, accountId, cancellationToken));
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

    // The people the caller may watch in the organization: those that are active members there, in the same transaction as the
    // caller's own membership check; null when the caller is not an active member.
    private async Task<IReadOnlyList<Guid>?> WatchableAsync(Guid accountId, Guid organizationId, IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken)
    {
        try
        {
            organization.Enter(organizationId);
            await unitOfWork.BeginAsync(cancellationToken);
            IReadOnlyList<Guid>? watchable = null;
            if (await membership.IsActiveMemberAsync(accountId, cancellationToken))
            {
                watchable = await membership.SelectActiveMembersAsync(accountIds, cancellationToken);
            }

            await unitOfWork.CommitAsync(cancellationToken);
            return watchable;
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

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Telling a connection that its session was revoked failed ({ExceptionType}); it is closed anyway.")]
    private static partial void LogNoticeFailed(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "The subscription check failed ({ExceptionType}) in organization {OrganizationId}.")]
    private static partial void LogCheckFailed(ILogger logger, string exceptionType, Guid organizationId);
}
