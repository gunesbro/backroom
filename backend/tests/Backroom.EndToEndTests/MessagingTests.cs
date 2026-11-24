using System.Net;
using AwesomeAssertions;

namespace Backroom.EndToEndTests;

/// <summary>Flow 4 on each cell: a channel and a message that reaches a second client live and through catch-up.</summary>
[Trait(TestCategory.Name, TestCategory.Short)]
public sealed class MessagingTests(EndToEndEnvironment environment)
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("a")]
    [InlineData("b")]
    public async Task PostMessage_WithASecondClientSubscribed_ArrivesLiveAndOnCatchUpAsync(string cellId)
    {
        Gate.RequireEnvironment();
        var organization = (await environment.OrganizationsAsync(CancellationToken)).Single(candidate => candidate.CellId == cellId);
        var cellIds = await environment.SharedCellIdsAsync(CancellationToken);
        // Two sign-ins are two clients (say the web app and the desktop app), each with its own session.
        using var sender = await SignedInPerson.SignInAsync(environment, EndToEndEnvironment.Alice, cellIds, CancellationToken);
        using var reader = await SignedInPerson.SignInAsync(environment, EndToEndEnvironment.Alice, cellIds, CancellationToken);
        var senderToken = await sender.TokenForAsync("cell:" + cellId, CancellationToken);
        var readerToken = await reader.TokenForAsync("cell:" + cellId, CancellationToken);

        var channel = await Api.PostAsync(
            environment.Http, organization.Api("channels"), senderToken.Value, new { name = $"e2e-{Guid.NewGuid():N}"[..20] }, CancellationToken);
        channel.Status.Should().Be(HttpStatusCode.Created);
        var channelId = channel.Json["id"]!.GetValue<Guid>();

        await using var live = await LiveClient.ConnectAsync(organization.CellUrl, readerToken, CancellationToken);
        await live.SubscribeConversationAsync(organization.Id, channelId, CancellationToken);

        var clientMessageId = Guid.NewGuid();
        var posted = await Api.PostAsync(
            environment.Http,
            organization.Api($"channels/{channelId}/messages"),
            senderToken.Value,
            new { clientMessageId, text = "Hello from the end-to-end test" },
            CancellationToken);
        posted.Status.Should().Be(HttpStatusCode.Created);

        var received = await live.NextMessageAsync(CancellationToken);
        received.OrgId.Should().Be(organization.Id);
        received.ChannelId.Should().Be(channelId);
        received.Message.ClientMessageId.Should().Be(clientMessageId);
        received.Message.Text.Should().Be("Hello from the end-to-end test");

        // A client that missed live events resumes after the last sequence number it saw.
        var catchUp = await Api.GetAsync(
            environment.Http, organization.Api($"channels/{channelId}/messages?after={received.Message.Seq - 1}"), readerToken.Value, CancellationToken);
        catchUp.Status.Should().Be(HttpStatusCode.OK);
        var first = catchUp.Json["messages"]!.AsArray()[0]!;
        first["id"]!.GetValue<Guid>().Should().Be(received.Message.Id);
        first["seq"]!.GetValue<long>().Should().Be(received.Message.Seq);
    }
}
