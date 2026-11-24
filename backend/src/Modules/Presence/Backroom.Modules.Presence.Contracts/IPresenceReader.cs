namespace Backroom.Modules.Presence.Contracts;

/// <summary>
/// Presence and status of people in the organization of the current unit of work (owner decision 16: presence lives in Valkey
/// outside row-level security, so this check is the guard). Accounts that are not active members of that organization are left out
/// of the answer, so a caller learns nothing about people it shares no organization with.
/// </summary>
public interface IPresenceReader
{
    /// <summary>The most accounts one call reads.</summary>
    public const int MaxAccounts = 200;

    /// <summary>Runs in the current unit of work, which row-level security limits to the entered organization.</summary>
    Task<IReadOnlyList<PresenceEntry>> ReadAsync(IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken);
}
