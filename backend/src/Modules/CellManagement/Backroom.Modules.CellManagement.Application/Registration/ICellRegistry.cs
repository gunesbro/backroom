namespace Backroom.Modules.CellManagement.Application.Registration;

/// <summary>The cell registry and its registration keys. Each method runs in a transaction of its own.</summary>
internal interface ICellRegistry
{
    /// <summary>
    /// Claims an unused, unexpired registration key for <paramref name="thumbprint"/>; a key claimed (or
    /// already used) by that same public key may be claimed again, so a retry after a lost response
    /// succeeds. Returns the cell id, or <see langword="null"/>.
    /// </summary>
    Task<string?> ClaimRegistrationKeyAsync(byte[] keyHash, string thumbprint, CancellationToken cancellationToken);

    /// <summary>Marks the claimed key used and the cell registered with <paramref name="version"/>.</summary>
    Task CompleteRegistrationAsync(byte[] keyHash, string thumbprint, string version, CancellationToken cancellationToken);

    /// <summary>Whether <paramref name="cellId"/> has completed registration.</summary>
    Task<bool> IsRegisteredAsync(string cellId, CancellationToken cancellationToken);

    /// <summary>Records that the registered cell is connected and runs <paramref name="version"/>.</summary>
    Task RecordHeartbeatAsync(string cellId, string version, CancellationToken cancellationToken);

    /// <summary>How many shared cells are registered or pending with a key that can still be used.</summary>
    Task<int> CountSharedCellsAsync(CancellationToken cancellationToken);

    /// <summary>The ids of registered shared cells, oldest registration first, at most <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<string>> SharedCellIdsAsync(int limit, CancellationToken cancellationToken);

    /// <summary>Adds a pending cell and its registration key; returns <see langword="false"/> if the id is taken.</summary>
    Task<bool> AddCellAsync(NewCell cell, byte[] keyHash, DateTimeOffset expiresAt, CancellationToken cancellationToken);

    /// <summary>
    /// Cells in id order after <paramref name="after"/>, at most <paramref name="limit"/>, with their pending
    /// and rejected downstream message counts, in one query.
    /// </summary>
    Task<IReadOnlyList<Listing.CellRow>> ListCellsAsync(string? after, int limit, CancellationToken cancellationToken);
}
