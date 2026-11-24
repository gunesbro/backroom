namespace Backroom.Modules.Presence.Contracts;

/// <summary>Tells Presence that a membership ended, in every role (the member-removed consumer runs in the worker).</summary>
public interface IPresenceMemberships
{
    /// <summary>Stops publishing the account's presence to the organization's presence groups at once. Throws when Valkey cannot be written.</summary>
    Task ForgetAsync(Guid organizationId, Guid accountId, CancellationToken cancellationToken);
}
