using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Backroom.BuildingBlocks.Application.Identifiers;
using Backroom.BuildingBlocks.Infrastructure.Messaging;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.Modules.Messaging.Application.Channels;
using Backroom.Modules.Messaging.Application.Conversations;
using Backroom.Modules.Messaging.Application.Live;
using Backroom.Modules.Messaging.Application.Messages;
using Backroom.Modules.Messaging.Contracts;

namespace Backroom.Modules.Messaging.Infrastructure;

public static class MessagingModule
{
    /// <summary>The module's organization-scoped schema, for the migrator.</summary>
    public static IServiceCollection AddMessagingDatabase(this IServiceCollection services) =>
        services.AddModuleDbContext<MessagingDbContext>(MessagingDbContext.Schema, organizationScoped: true);

    /// <summary>
    /// Conversations (public channels, memberships) and plain-text messages. Needs the database session and Realtime's <c>ILiveEventPublisher</c>
    /// (<c>AddRealtime</c>). The worker's relay publishes this module's outbox (only where the host runs <c>AddOutboxRelay</c>).
    /// </summary>
    public static IServiceCollection AddMessaging(this IServiceCollection services)
    {
        services.AddMessagingDatabase();
        services.AddOutbox();
        services.Configure<OutboxRelayOptions>(options =>
        {
            if (!options.Schemas.Contains(MessagingDbContext.Schema, StringComparer.Ordinal))
            {
                options.Schemas.Add(MessagingDbContext.Schema);
            }
        });
        services.TryAddSingleton(TimeProvider.System);
        // Monotonic within the process, so messages sent in the same millisecond keep their order by id.
        services.TryAddSingleton<IIdGenerator>(provider => new UuidV7IdGenerator(provider.GetRequiredService<TimeProvider>()));
        services.TryAddScoped<IChannelStore, ChannelStore>();
        services.TryAddScoped<IConversationStore, ConversationStore>();
        services.TryAddScoped<IConversationReadAccess, ConversationReadAccess>();
        services.TryAddScoped<IMessageStore, MessageStore>();
        services.TryAddScoped<AfterCommitLiveEvents>();
        services.TryAddScoped<MembershipService>();
        services.TryAddScoped<ChannelService>();
        services.TryAddScoped<ConversationService>();
        services.TryAddScoped<MessageService>();
        return services;
    }
}
