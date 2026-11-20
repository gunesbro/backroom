namespace Backroom.BuildingBlocks.Infrastructure.Messaging;

/// <summary>Settings of the outbox relay, which runs in the worker role.</summary>
public sealed class OutboxRelayOptions
{
    public const string SectionName = "OutboxRelay";

    public const int MaxBatchSize = 1000;

    /// <summary>Connection string of the relay role (<c>backroom_relay</c>); from secrets, like every connection string.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Connections of the relay's own pool; the relay uses one at a time.</summary>
    public int MaxPoolSize { get; set; } = 2;

    /// <summary>Module schemas whose outbox tables this relay publishes.</summary>
    public IList<string> Schemas { get; } = [];

    /// <summary>Events read, published and marked per transaction.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>Wait between polls when the outboxes are empty.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>How long published events stay in the table (for diagnosis) before they are deleted.</summary>
    public TimeSpan PublishedRetention { get; set; } = TimeSpan.FromDays(1);

    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Module schemas whose inbox tables this relay cleans up (the consuming modules in this cell).</summary>
    public IList<string> InboxSchemas { get; } = [];

    /// <summary>
    /// How long a handled event is remembered. A delivery older than this (for example one replayed from
    /// a dead-letter queue much later) would be handled again.
    /// </summary>
    public TimeSpan InboxRetention { get; set; } = TimeSpan.FromDays(14);
}
