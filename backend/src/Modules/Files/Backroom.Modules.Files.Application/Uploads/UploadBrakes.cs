using Backroom.BuildingBlocks.Domain.Errors;

namespace Backroom.Modules.Files.Application.Uploads;

/// <summary>
/// The abuse brakes of flow 6: per account 30 upload starts per minute and 10 GB per day, per organization 100 GB per
/// day, counted by declared size at the start (a refused or abandoned upload still counts). Days are UTC calendar
/// days. Over a limit answers <c>429 files.rate_limited</c> with <c>Retry-After</c>. They limit abuse, not storage
/// (the quota of flow 12 is separate). Runs in the start's transaction: a refusal rolls back what it counted.
/// </summary>
internal sealed class UploadBrakes(IUploadLimitStore store)
{
    public const int StartsPerMinute = 30;
    public const long AccountBytesPerDay = 10L * 1024 * 1024 * 1024;
    public const long OrganizationBytesPerDay = 100L * 1024 * 1024 * 1024;

    public const string RateLimitedCode = "files.rate_limited";

    private static readonly TimeSpan s_window = TimeSpan.FromMinutes(1);

    /// <summary>Counts the start; an error when a limit is reached.</summary>
    public async Task<Error?> CountStartAsync(Guid organizationId, Guid accountId, long size, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var utcNow = now.ToUniversalTime();
        var day = DateOnly.FromDateTime(utcNow.UtcDateTime);
        var untilTomorrow = new DateTimeOffset(day.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) - utcNow;

        // The account's counter first: its row lock serializes the account's starts, so the minute count below sees
        // every earlier start of the account as committed. Then the organization's (always in this order: no deadlock).
        if (!await store.TryAddAccountBytesAsync(organizationId, accountId, day, size, AccountBytesPerDay, cancellationToken))
        {
            return RateLimited(untilTomorrow);
        }

        var recent = await store.RecentStartsAsync(organizationId, accountId, utcNow - s_window, StartsPerMinute, cancellationToken);
        if (recent.Count >= StartsPerMinute)
        {
            // Free again when the oldest of those starts leaves the window.
            return RateLimited(recent[^1] + s_window - utcNow);
        }

        return await store.TryAddOrganizationBytesAsync(organizationId, day, size, OrganizationBytesPerDay, cancellationToken)
            ? null
            : RateLimited(untilTomorrow);
    }

    private static Error RateLimited(TimeSpan retryAfter) =>
        Error.RateLimited(RateLimitedCode, "Too many uploads; try again later.", retryAfter < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : retryAfter);
}
