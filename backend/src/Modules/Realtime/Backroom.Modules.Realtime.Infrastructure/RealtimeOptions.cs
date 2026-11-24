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

    /// <summary>Groups one connection may join (organization, account and conversation groups together; presence groups later).</summary>
    public int MaxSubscriptionsPerConnection { get; set; } = 600;

    /// <summary>Connections one account may hold open on one node; further ones are closed.</summary>
    public int MaxConnectionsPerAccount { get; set; } = 20;

    /// <summary>The largest message a client may send; the hub methods take two ids.</summary>
    public int MaxReceiveMessageSizeBytes { get; set; } = 4 * 1024;

    /// <summary>
    /// The longest a live event publish waits for Valkey before the event is given up (a client catches up with a read).
    /// It is short, because the sending request waits for it: while Valkey is down, a send costs this much and no more.
    /// </summary>
    public TimeSpan PublishTimeout { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>The longest any other Valkey command waits (the group-removal broadcasts of the event consumers, which are retried when they fail).</summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Subscribe calls one connection may make per <see cref="SubscribeWindow"/>; the next one aborts the connection.</summary>
    public int MaxSubscribeCallsPerWindow { get; set; } = 60;

    public TimeSpan SubscribeWindow { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Refused subscribe calls (not a member, no such conversation) one connection may collect; the next one aborts it.</summary>
    public int MaxRefusedSubscribesPerConnection { get; set; } = 10;

    /// <summary>Publishes in flight on one node; more are dropped instead of queued.</summary>
    public int MaxConcurrentPublishes { get; set; } = 256;

    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(15);

    public TimeSpan ClientTimeoutInterval { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a "not revoked" answer is trusted before the request path asks Valkey again. With the push (the
    /// <c>SessionRevoked</c> broadcast updates every node's cache at once) a revocation takes effect at once; this is the bound
    /// when the push is missed: a revocation stored in Valkey is enforced within this time plus the clock of the check.
    /// </summary>
    public TimeSpan RevocationCacheTtl { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How much longer an expired "not revoked" answer may stand in for Valkey while Valkey cannot answer. Past
    /// <see cref="RevocationCacheTtl"/> plus this, a check that cannot reach Valkey fails closed (the request gets 503, not 401, so a
    /// client waits and retries instead of signing the person out).
    /// </summary>
    public TimeSpan RevocationStaleTolerance { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Entries in a node's revocation cache, per kind (an account entry and one or more session entries per active person; sized for 5,000 users); over the limit the oldest "not revoked" answers are evicted (revocations in force never are).</summary>
    public int RevocationCacheMaxEntries { get; set; } = 100_000;

    /// <summary>
    /// What a request gets when the revocation store cannot be read and no recent answer is cached. <see langword="true"/> (the
    /// default): <c>503</c> with <c>Retry-After</c>, because a revoked session must not be served on a guess. <see langword="false"/>:
    /// the request is served (fail open), at most until its token expires (five minutes); the cell's API then stays up while Valkey is down.
    /// </summary>
    public bool RevocationFailClosed { get; set; } = true;

    /// <summary>
    /// After a failed read of the revocation store, further reads are skipped for this long and answer "unavailable" at once, so
    /// a hung Valkey costs one <see cref="CommandTimeout"/> per outage window, not one per request.
    /// </summary>
    public TimeSpan RevocationOutageBackoff { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The longest a revocation is kept in Valkey, whatever its <c>keep_until</c> says (the contract's is 5.5 minutes).</summary>
    public TimeSpan RevocationMaxRetention { get; set; } = TimeSpan.FromHours(1);

    /// <summary>How long a connection is given to receive <c>SessionRevoked</c> before the server closes it.</summary>
    public TimeSpan RevocationCloseGrace { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>The wait before the member-removal listener tries to subscribe again after a failure.</summary>
    public TimeSpan ListenerRetryDelay { get; set; } = TimeSpan.FromSeconds(5);
}
