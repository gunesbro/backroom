using AwesomeAssertions;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.Messaging.IntegrationTests;

/// <summary>With <c>Features:MessageSync</c> on (the epoch is required on <c>before</c> and <c>after</c>), the cursors of an around read continue with the answer's epoch.</summary>
[Collection(CellCollection.Name)]
public sealed class MessageAroundSyncFlagTests(PostgresDatabase database, KeycloakRealm keycloak) : MessagingTestBase(database, keycloak)
{
    protected override bool MessageSyncEnabled => true;

    [Fact]
    public async Task Around_TheCursors_ContinueWithTheEpochWhileTheFlagIsOnAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice, "General");
        var ids = new List<Guid>();
        for (var index = 0; index < 9; index++)
        {
            ids.Add((await SendMessageAsync(organizationId, channelId, Alice, $"message {index}"))["id"]!.GetValue<Guid>());
        }

        var path = $"/api/v1/orgs/{organizationId}/conversations/{channelId}/messages";
        using var response = await GetAsync($"{path}?around={ids[4]}&limit=3", Alice);
        response.EnsureSuccessStatusCode();
        var body = await ReadJsonAsync(response);
        var epoch = body["epoch"]!.GetValue<string>();

        var older = await ReadJsonAsync(await GetAsync($"{path}?before={body["before"]!.GetValue<long>()}&epoch={epoch}&limit=2", Alice));
        var newer = await ReadJsonAsync(await GetAsync($"{path}?after={body["after"]!.GetValue<long>()}&epoch={epoch}&limit=2", Alice));
        using var withoutEpoch = await GetAsync($"{path}?before={body["before"]!.GetValue<long>()}", Alice);

        body["items"]!.AsArray().Select(item => item!["seq"]!.GetValue<long>()).Should().Equal(6, 5, 4);
        older["items"]!.AsArray().Select(item => item!["seq"]!.GetValue<long>()).Should().Equal(3, 2);
        newer["items"]!.AsArray().Select(item => item!["seq"]!.GetValue<long>()).Should().Equal(7, 8);
        ((int)withoutEpoch.StatusCode).Should().Be(409);
    }
}
