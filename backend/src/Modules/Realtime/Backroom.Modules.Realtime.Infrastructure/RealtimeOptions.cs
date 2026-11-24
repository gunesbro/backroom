namespace Backroom.Modules.Realtime.Infrastructure;

/// <summary>
/// Limits and timings of the gateway (configuration section <c>Realtime</c>). Every number that bounds memory, work or
/// waiting is here, so none is hidden in code. The cell id comes from <c>Cell:Id</c>.
/// </summary>
public sealed class RealtimeOptions
{
    public const string SectionName = "Realtime";

    /// <summary>The cell's id, part of every group name and Valkey channel (cells share development servers).</summary>
    public string CellId { get; set; } = string.Empty;

    /// <summary>Groups one connection may join (organization and channel groups together).</summary>
    public int MaxSubscriptionsPerConnection { get; set; } = 200;

    /// <summary>Connections one account may hold open on one node; further ones are closed.</summary>
    public int MaxConnectionsPerAccount { get; set; } = 20;

    /// <summary>The largest message a client may send; the hub methods take two ids.</summary>
    public int MaxReceiveMessageSizeBytes { get; set; } = 4 * 1024;

    /// <summary>
    /// The longest a live event publish waits for Valkey before the event is given up (a client catches up with a read).
    /// It is short, because the sending request waits for it: while Valkey is down, a send costs this much and no more.
    /// </summary>
    public TimeSpan PublishTimeout { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>The longest any other Valkey command waits (the member-removal broadcast, which is retried when it fails).</summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Subscribe calls one connection may make per <see cref="SubscribeWindow"/>; the next one aborts the connection.</summary>
    public int MaxSubscribeCallsPerWindow { get; set; } = 60;

    public TimeSpan SubscribeWindow { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Refused subscribe calls (not a member, no such channel) one connection may collect; the next one aborts it.</summary>
    public int MaxRefusedSubscribesPerConnection { get; set; } = 10;

    /// <summary>Publishes in flight on one node; more are dropped instead of queued.</summary>
    public int MaxConcurrentPublishes { get; set; } = 256;

    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(15);

    public TimeSpan ClientTimeoutInterval { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The wait before the member-removal listener tries to subscribe again after a failure.</summary>
    public TimeSpan ListenerRetryDelay { get; set; } = TimeSpan.FromSeconds(5);
}
