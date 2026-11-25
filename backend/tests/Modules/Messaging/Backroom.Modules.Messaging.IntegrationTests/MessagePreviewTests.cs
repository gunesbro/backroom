using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Backroom.Modules.Messaging.Contracts;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.Messaging.IntegrationTests;

/// <summary>
/// <see cref="IMessagePreviews"/> (docs/contracts/phase-2.md, "Decisions", 13): text for the bell and notifications goes only
/// through the conversation's read check, truncated to whole text elements.
/// </summary>
[Collection(CellCollection.Name)]
[Trait("Category", "Privacy")]
public sealed class MessagePreviewTests(PostgresDatabase database, KeycloakRealm keycloak) : MessagingTestBase(database, keycloak)
{
    [Fact]
    public async Task Read_ForAMemberOfAPublicChannel_ReturnsTheTruncatedTextAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var alice = await AccountAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice, "General");
        var sent = await SendMessageAsync(organizationId, channelId, Alice, new string('a', 300));
        var messageId = sent["id"]!.GetValue<Guid>();

        var previews = await ReadAsync(organizationId, alice, [messageId], 150);

        previews.Should().ContainKey(messageId).WhoseValue.Should().Be(new string('a', 150));
    }

    [Fact]
    public async Task Read_ForAnAccountThatCannotReadTheConversation_ReturnsNothingAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, role: null);
        var bob = await AccountAsync(Bob);
        var channelId = await CreateChannelAsync(organizationId, Alice, "General");
        var sent = await SendMessageAsync(organizationId, channelId, Alice, "private words");
        var messageId = sent["id"]!.GetValue<Guid>();
        // A private conversation Bob is not a member of: the same check as reading it refuses.
        await ExecuteAsync("UPDATE messaging.conversations SET kind = 'private_channel' WHERE id = $1", channelId);

        var previews = await ReadAsync(organizationId, bob, [messageId], 150);

        previews.Should().BeEmpty();
    }

    [Fact]
    public async Task Read_ForAMessageOfAnotherOrganization_ReturnsNothingAsync()
    {
        var first = await ProvisionForAsync(Alice);
        var second = await ProvisionForAsync(Alice);
        var alice = await AccountAsync(Alice);
        var channelId = await CreateChannelAsync(first, Alice, "General");
        var messageId = (await SendMessageAsync(first, channelId, Alice, "text"))["id"]!.GetValue<Guid>();

        var previews = await ReadAsync(second, alice, [messageId], 150);

        previews.Should().BeEmpty();
    }

    private async Task<IReadOnlyDictionary<Guid, string>> ReadAsync(Guid organizationId, Guid accountId, Guid[] messageIds, int maxLength)
    {
        IReadOnlyDictionary<Guid, string> result = new Dictionary<Guid, string>();
        await InOrganizationAsync(organizationId, async services =>
            result = await services.GetRequiredService<IMessagePreviews>().ReadAsync(organizationId, accountId, messageIds, maxLength, CancellationToken));
        return result;
    }
}
