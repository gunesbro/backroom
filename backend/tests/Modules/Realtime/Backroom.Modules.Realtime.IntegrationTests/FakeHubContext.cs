using Microsoft.AspNetCore.SignalR;
using Backroom.Modules.Realtime.Infrastructure.Gateway;

namespace Backroom.Modules.Realtime.IntegrationTests;

/// <summary>
/// A hub context whose group sends run the test's function (hang, fail or succeed), so the publisher's bounds can be
/// tested without a server.
/// </summary>
internal sealed class FakeHubContext(Func<string, string, Task> send) : IHubContext<RealtimeHub>
{
    public IHubClients Clients { get; } = new FakeClients(send);

    public IGroupManager Groups => throw new NotSupportedException();

    private sealed class FakeClients(Func<string, string, Task> send) : IHubClients
    {
        public IClientProxy All => throw new NotSupportedException();

        public IClientProxy Group(string groupName) => new FakeProxy(groupName, send);

        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();

        public IClientProxy Client(string connectionId) => throw new NotSupportedException();

        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => throw new NotSupportedException();

        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();

        public IClientProxy Groups(IReadOnlyList<string> groupNames) => throw new NotSupportedException();

        public IClientProxy User(string userId) => throw new NotSupportedException();

        public IClientProxy Users(IReadOnlyList<string> userIds) => throw new NotSupportedException();
    }

    private sealed class FakeProxy(string group, Func<string, string, Task> send) : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) => send(group, method);
    }
}
