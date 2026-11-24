namespace Backroom.Modules.Presence.Infrastructure;

/// <summary>
/// Timings and limits of presence (configuration section <c>Presence</c>; docs/contracts/phase-2.md, flow 9). Every number that
/// bounds memory, work or waiting is here. <see cref="Enabled"/> comes from <c>Features:Presence</c>, the cell id from <c>Cell:Id</c>.
/// </summary>
public sealed class PresenceOptions
{
    public const string SectionName = "Presence";

    public bool Enabled { get; set; }

    public string CellId { get; set; } = string.Empty;

    /// <summary>A connection with no input reported for this long counts as idle (the requirements' 10 minutes).</summary>
    public TimeSpan IdleAfter { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>A person with no connection to the cell for this long is offline; a node refreshes its connections well within it.</summary>
    public TimeSpan OfflineAfter { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>How often a node tells Valkey that its connections are still open.</summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>At most one <c>PresenceChanged</c> per person this often; changes in between are coalesced into the next one.</summary>
    public TimeSpan EventInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How often a node looks for people whose presence is due to be evaluated again (expiries, idle, coalesced events).</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>People one sweep evaluates at most; the rest wait for the next sweep.</summary>
    public int SweepBatchSize { get; set; } = 500;

    /// <summary>How long a claimed evaluation stays with the node that claimed it before another node may take it.</summary>
    public TimeSpan SweepLease { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a watch is kept for publishing without being renewed; the watching node renews it at half this.</summary>
    public TimeSpan WatchLifetime { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Organizations whose presence groups one person's changes reach at most.</summary>
    public int MaxOrganizationsPerPerson { get; set; } = 100;

    /// <summary>Connections this node tracks at most (the hub allows 20 per account).</summary>
    public int MaxConnections { get; set; } = 100_000;

    /// <summary>How long the account status copy is kept without a <c>FullSync</c> renewing it (every 15 minutes).</summary>
    public TimeSpan StatusRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>The longest one Valkey command waits.</summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(2);
}
