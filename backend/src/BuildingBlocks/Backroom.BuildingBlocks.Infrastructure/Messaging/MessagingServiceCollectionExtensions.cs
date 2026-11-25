using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Backroom.BuildingBlocks.Application.Messaging;
using Backroom.BuildingBlocks.Domain.Events;

namespace Backroom.BuildingBlocks.Infrastructure.Messaging;

public static class MessagingServiceCollectionExtensions
{
    /// <summary>Registers <see cref="IOutbox"/> on the unit of work (requires <c>AddDatabaseSession</c>).</summary>
    public static IServiceCollection AddOutbox(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<IOutbox, Outbox>();
        return services;
    }

    /// <summary>Registers the process's RabbitMQ connection; the returned builder binds <see cref="RabbitMqOptions"/>.</summary>
    public static OptionsBuilder<RabbitMqOptions> AddRabbitMq(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<RabbitMqConnection>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<RabbitMqOptions>, MessagingOptionsValidator>());
        return services.AddOptions<RabbitMqOptions>().ValidateOnStart();
    }

    /// <summary>
    /// Runs the outbox relay as a hosted service (worker role; requires <see cref="AddRabbitMq"/>).
    /// The returned builder binds <see cref="OutboxRelayOptions"/>.
    /// </summary>
    public static OptionsBuilder<OutboxRelayOptions> AddOutboxRelay(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<OutboxRelayOptions>, MessagingOptionsValidator>());
        services.AddHostedService<OutboxRelay>();
        return services.AddOptions<OutboxRelayOptions>().ValidateOnStart();
    }

    /// <summary>
    /// Registers <typeparamref name="THandler"/> for <typeparamref name="TEvent"/> in the consuming
    /// module whose schema (with its inbox table) is <paramref name="consumerSchema"/>.
    /// </summary>
    public static IServiceCollection AddIntegrationEventHandler<TEvent, THandler>(this IServiceCollection services, string consumerSchema)
        where TEvent : IIntegrationEvent
        where THandler : class, IIntegrationEventHandler<TEvent>
    {
        ArgumentNullException.ThrowIfNull(services);
        var subscription = IntegrationEventSubscription.For<TEvent, THandler>(consumerSchema);
        services.TryAddScoped<THandler>();
        services.TryAddScoped<AfterCommitActions>();
        services.AddSingleton(subscription);

        // The relay cleans up every consuming module's inbox, so it cannot be forgotten in configuration.
        services.Configure<OutboxRelayOptions>(options =>
        {
            if (!options.InboxSchemas.Contains(subscription.ConsumerSchema, StringComparer.Ordinal))
            {
                options.InboxSchemas.Add(subscription.ConsumerSchema);
            }
        });
        return services;
    }

    /// <summary>
    /// Runs the consumers of every registered handler as a hosted service (worker role; requires
    /// <c>AddDatabaseSession</c> and <see cref="AddRabbitMq"/>). Register it before the outbox relay,
    /// so queues are declared before events are published. The returned builder binds
    /// <see cref="IntegrationEventConsumerOptions"/>.
    /// </summary>
    public static OptionsBuilder<IntegrationEventConsumerOptions> AddIntegrationEventConsumers(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<IntegrationEventConsumerOptions>, MessagingOptionsValidator>());
        services.AddHostedService(provider => new IntegrationEventConsumerHost(
            provider.GetServices<IntegrationEventSubscription>(),
            provider.GetRequiredService<RabbitMqConnection>(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IOptions<IntegrationEventConsumerOptions>>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<IntegrationEventConsumerHost>>(),
            provider.GetService<Fencing.ICellWriteFence>()));
        return services.AddOptions<IntegrationEventConsumerOptions>().ValidateOnStart();
    }
}
