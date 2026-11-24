using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Backroom.BuildingBlocks.Application.Organizations;
using Backroom.BuildingBlocks.Application.Persistence;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.Testing;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.Messaging.IntegrationTests;

/// <summary>
/// Catch-up must not miss messages: the cursor is the channel sequence number, taken in the send transaction under
/// the channel row lock, so it follows commit order even when message ids (assigned before the commit, by clocks that
/// differ between nodes) do not.
/// </summary>
[Collection(CellCollection.Name)]
[Trait("Category", "Privacy")]
public sealed class MessageOrderTests(PostgresDatabase database, KeycloakRealm keycloak) : MessagingTestBase(database, keycloak)
{
    [Fact]
    public async Task CatchUp_WhenAMessageWithALowerIdCommitsLater_ReturnsBothAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice, "General");
        var higher = Guid.CreateVersion7(DateTimeOffset.UtcNow.AddMinutes(1));
        var lower = Guid.CreateVersion7(DateTimeOffset.UtcNow.AddMinutes(-1));
        IdScript.Enqueue(higher);
        var first = await SendMessageAsync(organizationId, channelId, Alice, "committed first, higher id");
        IdScript.Enqueue(lower);
        var second = await SendMessageAsync(organizationId, channelId, Alice, "committed second, lower id");

        var body = await ReadJsonAsync(await GetAsync(
            $"/api/v1/orgs/{organizationId}/conversations/{channelId}/messages?after={first["seq"]!.GetValue<long>()}", Alice));

        first["id"]!.GetValue<Guid>().CompareTo(second["id"]!.GetValue<Guid>()).Should().BePositive();
        second["seq"]!.GetValue<long>().Should().Be(first["seq"]!.GetValue<long>() + 1);
        var caughtUp = body["items"]!.AsArray();
        caughtUp.Should().ContainSingle();
        caughtUp[0]!["id"]!.GetValue<Guid>().Should().Be(lower, "paging by id would have skipped it");
    }

    [Fact]
    public async Task Send_ReturnsAndPublishesTheSequenceNumberAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice, "General");
        var otherChannel = await CreateChannelAsync(organizationId, Alice, "Other");

        var one = await SendMessageAsync(organizationId, channelId, Alice, "one");
        var two = await SendMessageAsync(organizationId, channelId, Alice, "two");
        var elsewhere = await SendMessageAsync(organizationId, otherChannel, Alice, "elsewhere");

        one["seq"]!.GetValue<long>().Should().Be(1);
        two["seq"]!.GetValue<long>().Should().Be(2);
        elsewhere["seq"]!.GetValue<long>().Should().Be(1, "every channel counts on its own");
        Publisher.Messages.Select(message => message.Message.Seq).Should().Equal(1, 2, 1);
        (await QuerySingleAsync("SELECT last_seq FROM messaging.conversations WHERE id = $1", channelId))[0].Should().Be(2L);
    }

    [Fact]
    public async Task Send_ARepeat_KeepsItsSequenceNumberAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice, "General");
        var clientMessageId = Guid.NewGuid();

        var first = await SendMessageAsync(organizationId, channelId, Alice, "once", clientMessageId);
        var repeat = await SendMessageAsync(organizationId, channelId, Alice, "once", clientMessageId);

        repeat["seq"]!.GetValue<long>().Should().Be(first["seq"]!.GetValue<long>());
        (await QuerySingleAsync("SELECT last_seq FROM messaging.conversations WHERE id = $1", channelId))[0].Should().Be(1L, "a repeat takes no sequence number");
    }

    [Fact]
    public async Task Send_TheTextOfARawMessage_IsReturnedUnchangedInTextAndBlocksAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice, "General");

        var message = await SendMessageAsync(organizationId, channelId, Alice, "<b>x</b> & y");
        var listed = await ReadJsonAsync(await GetAsync($"/api/v1/orgs/{organizationId}/conversations/{channelId}/messages", Alice));

        message["text"]!.GetValue<string>().Should().Be("<b>x</b> & y");
        listed["items"]![0]!["text"]!.GetValue<string>().Should().Be("<b>x</b> & y");
        var node = await QuerySingleAsync("SELECT blocks->0->'children'->0->>'text' FROM messaging.messages WHERE id = $1", message["id"]!.GetValue<Guid>());
        node[0].Should().Be("<b>x</b> & y");
        Publisher.Messages.Single().Message.Text.Should().Be("<b>x</b> & y");
    }

    [Fact]
    public async Task SendAndRead_UnderTurkishCulture_KeepTheTextAsync()
    {
        using var scope = CultureScope.Turkish();
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice, "Iletisim");

        var sent = await SendMessageAsync(organizationId, channelId, Alice, "I i ı İ ISPARTA");
        var listed = await ReadJsonAsync(await GetAsync($"/api/v1/orgs/{organizationId}/conversations/{channelId}/messages?after=0", Alice));

        sent["text"]!.GetValue<string>().Should().Be("I i ı İ ISPARTA");
        listed["items"]![0]!["text"]!.GetValue<string>().Should().Be("I i ı İ ISPARTA");
    }

    [Fact]
    public async Task Send_WhenTheCommitFails_PublishesNothingAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice, "General");
        Publisher.Messages.Should().BeEmpty();
        // A deferred constraint trigger raises at commit time, after every statement of the request has succeeded.
        await ExecuteAsync(
            """
            CREATE FUNCTION messaging.fail_at_commit() RETURNS trigger LANGUAGE plpgsql AS
            $$ BEGIN RAISE EXCEPTION 'failing at commit'; END $$;
            CREATE CONSTRAINT TRIGGER fail_at_commit AFTER INSERT ON messaging.messages
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (NEW.text = 'FAIL-AT-COMMIT')
                EXECUTE FUNCTION messaging.fail_at_commit();
            """);
        try
        {
            HttpStatusCode? status = null;
            try
            {
                using var response = await PostAsync(
                    $"/api/v1/orgs/{organizationId}/conversations/{channelId}/messages", Alice, new { clientMessageId = Guid.NewGuid(), text = "FAIL-AT-COMMIT" });
                status = response.StatusCode;
            }
#pragma warning disable CA1031 // The test host may rethrow the unhandled exception instead of answering 500.
            catch (Exception)
#pragma warning restore CA1031
            {
                status = null;
            }

            (status is null || status >= HttpStatusCode.InternalServerError).Should().BeTrue();
            Publisher.Messages.Should().BeEmpty("nothing is announced that was not stored");
            (await QuerySingleAsync("SELECT count(*) FROM messaging.messages WHERE conversation_id = $1", channelId))[0].Should().Be(0L);
        }
        finally
        {
            await ExecuteAsync("DROP TRIGGER fail_at_commit ON messaging.messages; DROP FUNCTION messaging.fail_at_commit();");
        }
    }

    [Fact]
    public async Task UnitOfWork_RunsAtReadCommitted_WhichTheRepeatCheckReliesOnAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await using var scope = Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<OrganizationContext>().Enter(organizationId);
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await unitOfWork.BeginAsync(CancellationToken);
        var session = scope.ServiceProvider.GetRequiredService<DatabaseSession>();

        await using var command = new NpgsqlCommand("SHOW transaction_isolation", session.Connection, session.Transaction);
        var level = (string?)await command.ExecuteScalarAsync(CancellationToken);
        await unitOfWork.RollbackAsync(CancellationToken);

        level.Should().Be("read committed");
    }
}
