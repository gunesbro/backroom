namespace Backroom.BuildingBlocks.Infrastructure.Messaging;

/// <summary>Settings of the integration event consumers, which run in the worker role.</summary>
public sealed class IntegrationEventConsumerOptions
{
    public const string SectionName = "IntegrationEventConsumers";

    public const int MaxRetries = 10;

    /// <summary>Unacknowledged deliveries per queue; each queue handles one delivery at a time.</summary>
    public ushort Prefetch { get; set; } = 16;

    /// <summary>
    /// Wait before each retry of a failed delivery; after the last one the delivery goes to the
    /// dead-letter queue. Each delay has its own retry queue named after it.
    /// </summary>
    public IList<TimeSpan> RetryDelays { get; } =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10)];

    /// <summary>
    /// First pause before requeueing a delivery after a transient failure (database unreachable,
    /// deadlock) or a failed republish; it doubles each time, up to 30 seconds.
    /// </summary>
    public TimeSpan RequeuePause { get; set; } = TimeSpan.FromSeconds(1);
}
