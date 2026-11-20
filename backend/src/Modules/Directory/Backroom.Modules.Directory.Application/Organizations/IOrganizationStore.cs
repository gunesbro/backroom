namespace Backroom.Modules.Directory.Application.Organizations;

/// <summary>Directory's organization writes, in the scope's active unit of work.</summary>
internal interface IOrganizationStore
{
    /// <summary>
    /// Takes the account's creation lock for the rest of the unit of work without waiting, then counts the
    /// organizations it created since <paramref name="since"/> and returns the oldest of them;
    /// <see langword="null"/> if another unit of work holds the lock.
    /// </summary>
    Task<(int Count, DateTimeOffset? Oldest)?> TryLockAndCountCreatedSinceAsync(Guid accountId, DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>How many organizations each of <paramref name="cellIds"/> holds (missing cells hold none).</summary>
    Task<IReadOnlyDictionary<string, int>> CountByCellAsync(IReadOnlyCollection<string> cellIds, CancellationToken cancellationToken);

    /// <summary>Adds an organization being provisioned in <paramref name="cellId"/> and its owner's pending membership.</summary>
    Task AddProvisioningAsync(
        Guid organizationId, string name, string region, string cellId, Guid ownerId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>The cell created the organization: it and its owner's membership become active. False if it was not provisioning there.</summary>
    Task<bool> ActivateAsync(Guid organizationId, string cellId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>The cell refused it: the organization fails with <paramref name="code"/> and the owner's membership is removed.</summary>
    Task<bool> FailAsync(Guid organizationId, string cellId, string code, DateTimeOffset now, CancellationToken cancellationToken);
}
