namespace Backroom.Modules.Files.Application.Uploads;

/// <summary>
/// The counters behind the upload brakes (docs/contracts/phase-2.md, flow 6, "Abuse brakes"), in the request's unit of
/// work. Each add takes its counter row's lock until commit, so concurrent starts cannot pass a limit together.
/// </summary>
internal interface IUploadLimitStore
{
    /// <summary>Adds <paramref name="size"/> to the account's bytes of <paramref name="day"/> unless that exceeds <paramref name="limit"/>.</summary>
    Task<bool> TryAddAccountBytesAsync(Guid organizationId, Guid accountId, DateOnly day, long size, long limit, CancellationToken cancellationToken);

    /// <summary>Adds <paramref name="size"/> to the organization's bytes of <paramref name="day"/> unless that exceeds <paramref name="limit"/>.</summary>
    Task<bool> TryAddOrganizationBytesAsync(Guid organizationId, DateOnly day, long size, long limit, CancellationToken cancellationToken);

    /// <summary>The start times of the account's newest uploads after <paramref name="since"/>, at most <paramref name="count"/>, newest first.</summary>
    Task<IReadOnlyList<DateTimeOffset>> RecentStartsAsync(Guid organizationId, Guid accountId, DateTimeOffset since, int count, CancellationToken cancellationToken);
}
