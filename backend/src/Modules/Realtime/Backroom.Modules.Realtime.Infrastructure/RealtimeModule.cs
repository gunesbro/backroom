using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Backroom.BuildingBlocks.Infrastructure.Messaging;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.Modules.Messaging.Contracts;
using Backroom.Modules.Organization.Contracts;
using Backroom.Modules.Realtime.Contracts;
using Backroom.Modules.Realtime.Infrastructure.Gateway;
using Backroom.Modules.Realtime.Infrastructure.Revocation;

namespace Backroom.Modules.Realtime.Infrastructure;

public static class RealtimeModule
{
    /// <summary>The module's organization-scoped schema (the inbox of its consumer), for the migrator.</summary>
    public static IServiceCollection AddRealtimeDatabase(this IServiceCollection services) =>
        services.AddModuleDbContext<RealtimeDbContext>(RealtimeDbContext.Schema, organizationScoped: true);

    /// <summary>
    /// Live events for the modules, in every role: the publisher, the Valkey connection and the SignalR gateway with the
    /// MessagePack protocol and the Valkey backplane (an event published on any node reaches the connections on all
    /// nodes). Needs <c>Valkey:ConnectionString</c> and <c>Cell:Id</c>, checked at startup; Valkey itself may be down
    /// at that time. The app role also needs <see cref="AddRealtimeGateway"/> and <see cref="RealtimeEndpoints.MapRealtimeHub"/>.
    /// </summary>
    public static IServiceCollection AddRealtime(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<ValkeyOptions>()
            .BindConfiguration(ValkeyOptions.SectionName)
            .Validate(options => !string.IsNullOrWhiteSpace(options.ConnectionString), "Valkey:ConnectionString is required.")
            .ValidateOnStart();
        services.AddOptions<RealtimeOptions>()
            .BindConfiguration(RealtimeOptions.SectionName)
            .Configure<IConfiguration>((options, configuration) => options.CellId = configuration["Cell:Id"] ?? options.CellId)
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.CellId) && !options.CellId.Contains(':', StringComparison.Ordinal),
                "Cell:Id is required and must not contain ':'.")
            .Validate(
                options => options.MaxSubscriptionsPerConnection is >= 2 and <= 1000
                    && options.MaxConnectionsPerAccount is > 0 and <= 100
                    && options.MaxReceiveMessageSizeBytes is >= 256 and <= 64 * 1024
                    && options.MaxConcurrentPublishes is > 0 and <= 10_000
                    && options.MaxSubscribeCallsPerWindow is > 0 and <= 10_000
                    && options.MaxRefusedSubscribesPerConnection is > 0 and <= 10_000,
                "The Realtime limits are out of range.")
            .Validate(
                options => options.PublishTimeout > TimeSpan.Zero
                    && options.CommandTimeout > TimeSpan.Zero
                    && options.SubscribeWindow > TimeSpan.Zero
                    && options.KeepAliveInterval > TimeSpan.Zero
                    && options.ClientTimeoutInterval >= options.KeepAliveInterval * 2
                    && options.HandshakeTimeout > TimeSpan.Zero
                    && options.ListenerRetryDelay > TimeSpan.Zero
                    && options.RevocationCacheTtl > TimeSpan.Zero && options.RevocationCacheTtl <= TimeSpan.FromMinutes(1)
                    && options.RevocationStaleTolerance >= TimeSpan.Zero && options.RevocationStaleTolerance <= TimeSpan.FromMinutes(5)
                    && options.RevocationCacheMaxEntries is >= 16 and <= 1_000_000
                    && options.RevocationMaxRetention > TimeSpan.Zero && options.RevocationMaxRetention <= TimeSpan.FromHours(24)
                    && options.RevocationCloseGrace >= TimeSpan.Zero && options.RevocationCloseGrace <= TimeSpan.FromSeconds(5)
                    && options.RevocationOutageBackoff >= TimeSpan.Zero && options.RevocationOutageBackoff <= TimeSpan.FromMinutes(1),
                "The Realtime timings are out of range (the client timeout must be at least twice the keep-alive interval).")
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ValkeyConnection>();
        services.TryAddSingleton<ConnectionRegistry>();
        services.TryAddSingleton<GroupRemovalBroadcast>();
        services.TryAddSingleton<IUserIdProvider, AccountUserIdProvider>();
        services.TryAddSingleton<ILiveEventPublisher, ValkeyLiveEventPublisher>();

        // Revoked sessions, in every role: the worker applies them (the connector), the app role checks tokens against them.
        services.TryAddSingleton<IRevocationStore, ValkeyRevocationStore>();
        services.TryAddSingleton<RevocationChecker>();
        services.TryAddSingleton<SessionRevocationBroadcast>();
        services.TryAddSingleton<DelayedAbort>();
        services.TryAddSingleton<ISessionRevocation, SessionRevocationService>();

        // Only MessagePack: the live events are binary and the JSON protocol is refused at the handshake (it stays
        // registered because SignalR always registers it, but no hub supports it).
        services.AddSignalR()
            .AddMessagePackProtocol()
            .AddStackExchangeRedis();
        services.AddOptions<HubOptions>().Configure<IOptions<RealtimeOptions>>((hub, realtime) =>
        {
            var limits = realtime.Value;
            hub.SupportedProtocols = ["messagepack"];
            hub.MaximumReceiveMessageSize = limits.MaxReceiveMessageSizeBytes;
            hub.KeepAliveInterval = limits.KeepAliveInterval;
            hub.ClientTimeoutInterval = limits.ClientTimeoutInterval;
            hub.HandshakeTimeout = limits.HandshakeTimeout;
            hub.MaximumParallelInvocationsPerClient = 1;
            hub.StreamBufferCapacity = 1;
            hub.EnableDetailedErrors = false;
        });
        services.AddOptions<RedisOptions>().Configure<ValkeyConnection>((redis, valkey) =>
            redis.ConnectionFactory = _ => valkey.GetAsync(CancellationToken.None));
        return services;
    }

    /// <summary>
    /// App role, after <see cref="AddRealtime"/> and the host's database session: the hub's checks and the listener that
    /// removes connections from groups when a member is removed from an organization or a conversation.
    /// </summary>
    public static IServiceCollection AddRealtimeGateway(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHostedService<GroupRemovalListener>();
        services.AddHostedService<SessionRevocationListener>();
        return services;
    }

    /// <summary>
    /// Worker role, after <see cref="AddRealtime"/>: consumes <c>organization.member-removed.v1</c> and
    /// <c>messaging.conversation-member-removed.v1</c> and tells the app nodes to remove the account from the
    /// organization's groups or the conversation's group (the inbox is in the module's schema).
    /// </summary>
    public static IServiceCollection AddRealtimeMemberRemoval(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddRealtimeDatabase();
        services.AddIntegrationEventHandler<MemberRemoved, MemberRemovedConsumer>(RealtimeDbContext.Schema);
        return services.AddIntegrationEventHandler<ConversationMemberRemoved, ConversationMemberRemovedConsumer>(RealtimeDbContext.Schema);
    }
}
