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

    /// <summary>The longest a publish waits for Valkey before the event is given up (a client catches up with a read).</summary>
    public TimeSpan PublishTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Publishes in flight on one node; more are dropped instead of queued.</summary>
    public int MaxConcurrentPublishes { get; set; } = 256;

    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(15);

    public TimeSpan ClientTimeoutInterval { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The wait before the member-removal listener tries to subscribe again after a failure.</summary>
    public TimeSpan ListenerRetryDelay { get; set; } = TimeSpan.FromSeconds(5);
}
