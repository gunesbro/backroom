using Backroom.Modules.Presence.Contracts;

namespace Backroom.Modules.Realtime.Infrastructure.Gateway;

/// <summary>The member-removed consumer's presence hook when the Presence module is not added: nothing to forget.</summary>
internal sealed class NoPresenceMemberships : IPresenceMemberships
{
    public Task ForgetAsync(Guid organizationId, Guid accountId, CancellationToken cancellationToken) => Task.CompletedTask;
}
