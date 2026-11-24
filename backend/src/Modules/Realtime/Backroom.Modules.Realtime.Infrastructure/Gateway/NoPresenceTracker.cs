using Backroom.Modules.Presence.Contracts;

namespace Backroom.Modules.Realtime.Infrastructure.Gateway;

/// <summary>The hub's tracker when the Presence module is not added: presence is off and the hub refuses its methods.</summary>
internal sealed class NoPresenceTracker : IPresenceTracker
{
    public bool Enabled => false;

    public Task ConnectedAsync(string connectionId, Guid accountId, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task ActivityAsync(string connectionId, bool active, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task DisconnectedAsync(string connectionId, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task WatchAsync(string connectionId, Guid organizationId, IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public bool IsTracking(string connectionId) => false;

    public void Unwatch(Guid organizationId, Guid accountId)
    {
    }
}
