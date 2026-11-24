using Backroom.Modules.Presence.Contracts;

namespace Backroom.Modules.Presence.Infrastructure;

/// <summary><see cref="IPresenceMemberships"/>: an ended membership stops the person's presence from reaching that organization at once.</summary>
internal sealed class PresenceMemberships(PresenceStore store) : IPresenceMemberships
{
    public Task ForgetAsync(Guid organizationId, Guid accountId, CancellationToken cancellationToken) =>
        store.ForgetAsync(organizationId, accountId, cancellationToken);
}
