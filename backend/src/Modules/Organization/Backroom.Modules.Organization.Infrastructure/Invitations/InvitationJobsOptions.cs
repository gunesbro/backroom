namespace Backroom.Modules.Organization.Infrastructure.Invitations;

/// <summary>Settings of the cell's scheduled invitation jobs (worker role). Every value has a minimum and a maximum.</summary>
public sealed class InvitationJobsOptions
{
    public const string SectionName = "Organization:InvitationJobs";

    public const int MaxBatchSize = 1000;

    public const int MaxBatchesLimit = 200;

    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(10);

    public static readonly TimeSpan MaxInterval = TimeSpan.FromHours(1);

    /// <summary>Wait between runs. An invitation is marked expired 24 hours after <c>expires_at</c>, so minutes do not matter.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Rows per statement.</summary>
    public int BatchSize { get; set; } = 250;

    /// <summary>Batches per job and run; whatever is left waits for the next run.</summary>
    public int MaxBatchesPerRun { get; set; } = 40;

    /// <summary>The relay role's connection (the host's <c>OutboxRelay:ConnectionString</c>), because the jobs read every organization. A secret.</summary>
    public string RelayConnectionString { get; set; } = string.Empty;
}
