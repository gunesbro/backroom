using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Backroom.Modules.Realtime.Infrastructure.Gateway;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;
using Backroom.Testing.Valkey;

namespace Backroom.Modules.Realtime.IntegrationTests;

/// <summary>
/// An organization subscription joins two groups; when the second join fails, the first is left again, so a connection
/// never holds the organization group without the account group. The hub runs with the node's real checks and a group
/// manager that fails on the account group, because a real one cannot be made to fail halfway.
/// </summary>
[Collection(CellCollection.Name)]
[Trait("Category", "Privacy")]
public sealed class SubscribeRollbackTests(PostgresDatabase database, KeycloakRealm keycloak, ValkeyServer valkey)
    : RealtimeTestBase(database, keycloak, valkey)
{
    [Fact]
    public async Task SubscribeOrganization_WhenTheAccountGroupJoinFails_LeavesTheOrganizationGroupAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var alice = await AccountAsync(Alice);
        var registry = NodeA.Services.GetRequiredService<ConnectionRegistry>();
        const string ConnectionId = "rollback-test";
        var state = registry.TryAdd(ConnectionId, alice, maxPerAccount: 20)!;
        var groups = new FailingGroupManager(failOn: ":account:");
        try
        {
            await using var scope = NodeA.Services.CreateAsyncScope();
            using var hub = ActivatorUtilities.CreateInstance<RealtimeHub>(scope.ServiceProvider);
            using var context = new FakeCallerContext(ConnectionId);
            hub.Context = context;
            hub.Groups = groups;

            await Assert.ThrowsAsync<InvalidOperationException>(() => hub.SubscribeOrganization(organizationId));

            var organizationGroup = GroupNames.Organization(CellId, organizationId);
            groups.Added.Should().Equal(organizationGroup, GroupNames.Account(CellId, organizationId, alice));
            groups.Removed.Should().Contain(organizationGroup);
            (await SubscriptionCountAsync(NodeA, Alice)).Should().Be(0);
            context.Aborted.Should().BeFalse("leaving worked, so the connection stays");
        }
        finally
        {
            registry.Remove(state.ConnectionId);
        }
    }

    private sealed class FailingGroupManager(string failOn) : IGroupManager
    {
        public List<string> Added { get; } = [];

        public List<string> Removed { get; } = [];

        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            Added.Add(groupName);
            return groupName.Contains(failOn, StringComparison.Ordinal)
                ? Task.FromException(new InvalidOperationException("The backplane refused the join."))
                : Task.CompletedTask;
        }

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            Removed.Add(groupName);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeCallerContext(string connectionId) : HubCallerContext, IDisposable
    {
        private readonly CancellationTokenSource _aborted = new();

        public bool Aborted => _aborted.IsCancellationRequested;

        public override string ConnectionId { get; } = connectionId;

        public override string? UserIdentifier => null;

        public override ClaimsPrincipal? User => null;

        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();

        public override IFeatureCollection Features { get; } = new FeatureCollection();

        public override CancellationToken ConnectionAborted => _aborted.Token;

        public override void Abort() => _aborted.Cancel();

        public void Dispose() => _aborted.Dispose();
    }
}
