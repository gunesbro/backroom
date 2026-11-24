namespace Backroom.Modules.Messaging.Contracts;

/// <summary>
/// Whether an account may read a message, or a forwarded copy of one, that carries a file (docs/contracts/phase-2.md,
/// flow 6, step 5; "Modules": Files → Messaging). Files asks it on every download of an attached file, so removed
/// members and members who left a conversation are refused at once. Answers false for a file no readable message
/// carries; the caller has checked the organization membership.
/// </summary>
public interface IMessageFileAccess
{
    /// <summary>Runs in the current unit of work, which row-level security limits to the entered organization.</summary>
    Task<bool> CanReadFileAsync(Guid organizationId, Guid fileId, Guid accountId, CancellationToken cancellationToken);
}
