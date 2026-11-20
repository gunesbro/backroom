using System.Threading.RateLimiting;

namespace Backroom.Modules.Directory.Presentation;

/// <summary>
/// The limits on invitation codes (docs/contracts/phase-1.md, "Brute force"): 10 attempts per account per minute,
/// 1000 per client network per hour, and at most 100 failed ones per client network per hour. A network that used up
/// its failures is not shut out: its attempts still run, and only further failures are refused, so people behind the
/// same NAT as an attacker can still open their invitations. Sliding windows in memory per instance; a partition is
/// dropped once its window has passed without attempts, so the memory follows the accounts and networks of the last
/// hour (no hard cap: accounts are signed in, and a network key is an IPv4 address or an IPv6 /64). With 256-bit codes
/// guessing is infeasible: the limits stop abuse and noise.
/// </summary>
internal sealed class InvitationAttemptLimits : IDisposable
{
    public const int AttemptsPerAccountPerMinute = 10;
    public const int AttemptsPerNetworkPerHour = 1000;
    public const int FailuresPerNetworkPerHour = 100;

    private static readonly TimeSpan s_fallbackRetryAfter = TimeSpan.FromMinutes(5);

    private readonly PartitionedRateLimiter<string> _accounts;
    private readonly PartitionedRateLimiter<string> _networks;
    private readonly PartitionedRateLimiter<string> _failures;

    public InvitationAttemptLimits()
        : this(AttemptsPerAccountPerMinute, AttemptsPerNetworkPerHour, FailuresPerNetworkPerHour)
    {
    }

    /// <summary>Other limits, for tests.</summary>
    internal InvitationAttemptLimits(int attemptsPerAccountPerMinute, int attemptsPerNetworkPerHour, int failuresPerNetworkPerHour)
    {
        _accounts = Create(attemptsPerAccountPerMinute, TimeSpan.FromMinutes(1), segments: 6);
        _networks = Create(attemptsPerNetworkPerHour, TimeSpan.FromHours(1), segments: 12);
        _failures = Create(failuresPerNetworkPerHour, TimeSpan.FromHours(1), segments: 12);
    }

    /// <summary>
    /// Takes an attempt for the account, then the network; <see langword="null"/> if allowed, else when to try again.
    /// The account is checked first, so one account's refused attempts do not use up its network's permits.
    /// </summary>
    public TimeSpan? TryAcquire(string account, string network)
    {
        using var accountLease = _accounts.AttemptAcquire(account);
        if (!accountLease.IsAcquired)
        {
            return RetryAfter(accountLease);
        }

        using var networkLease = _networks.AttemptAcquire(network);
        return networkLease.IsAcquired ? null : RetryAfter(networkLease);
    }

    /// <summary>
    /// Counts a failed attempt from the network; <see langword="null"/> while its failures are within the limit, else
    /// when it may fail again.
    /// </summary>
    public TimeSpan? RecordFailure(string network)
    {
        using var lease = _failures.AttemptAcquire(network);
        return lease.IsAcquired ? null : RetryAfter(lease);
    }

    public void Dispose()
    {
        _accounts.Dispose();
        _networks.Dispose();
        _failures.Dispose();
    }

    private static PartitionedRateLimiter<string> Create(int permits, TimeSpan window, int segments) =>
        PartitionedRateLimiter.Create<string, string>(key => RateLimitPartition.GetSlidingWindowLimiter(
            key,
            _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = permits,
                Window = window,
                SegmentsPerWindow = segments,
                QueueLimit = 0,
            }));

    private static TimeSpan RetryAfter(RateLimitLease lease) =>
        lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter) && retryAfter > TimeSpan.Zero ? retryAfter : s_fallbackRetryAfter;
}
