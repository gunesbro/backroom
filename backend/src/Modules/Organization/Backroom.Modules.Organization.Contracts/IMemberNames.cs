namespace Backroom.Modules.Organization.Contracts;

/// <summary>
/// Display names of the entered organization's active members, for modules that write a name into text they derive
/// (Messaging: the plain text of a mention). Every active member may see these names (the member list shows them).
/// </summary>
public interface IMemberNames
{
    /// <summary>The most accounts <see cref="FindActiveAsync"/> looks up at once.</summary>
    public const int MaxAccountsPerLookup = 100;

    /// <summary>
    /// The display names of the accounts among <paramref name="accountIds"/> (at most <see cref="MaxAccountsPerLookup"/>)
    /// that are active members, in one statement in the current unit of work; an account that is not an active member
    /// is left out.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, string>> FindActiveAsync(IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken);
}
