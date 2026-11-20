using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Backroom.BuildingBlocks.Application.Organizations;
using Backroom.BuildingBlocks.Application.Persistence;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.Modules.Email.Application.Deliveries;
using Backroom.Modules.Email.Application.Sending;
using Backroom.Modules.Email.Infrastructure;
using Backroom.Testing.Mailpit;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.Email.IntegrationTests;

/// <summary>The delivery log: what a send records, and that it holds no address, content or link.</summary>
[Trait("Category", "Privacy")]
public sealed class EmailDispatcherTests(PostgresDatabase database, MailpitServer mailpit) : IAsyncLifetime
{
    private static readonly EmailTemplateId s_template = new("invitation", 1);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await EmailSchema.MigrateOnceAsync(database, CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Send_ThroughMailpit_RecordsASentDeliveryWithoutAddressOrContentAsync()
    {
        using var provider = Build(mailpit.SmtpPort);
        var organizationId = Guid.CreateVersion7();
        var address = EmailServices.NewAddress();
        const string Subject = "Join Acme on Backroom";
        const string Link = "https://app.backroom.test/invitations#code-AbC123";
        var message = EmailMessage.Create(address, Subject, $"Open {Link} to join.", $"<a href=\"{Link}\">Join</a>");

        var outcome = await provider.GetRequiredService<EmailDispatcher>().SendAsync(organizationId, s_template, message, CancellationToken);

        outcome.Status.Should().Be(DeliveryStatus.Sent);
        var received = (await mailpit.MessagesToAsync(address, CancellationToken)).Should().ContainSingle().Subject;
        var row = await ReadDeliveryAsync(outcome.DeliveryId);
        row.OrganizationId.Should().Be(organizationId);
        row.Template.Should().Be("invitation");
        row.TemplateVersion.Should().Be(1);
        row.Status.Should().Be(DeliveryStatus.Sent);
        row.ProviderMessageId.Should().Be(received.MessageId);
        row.RecipientHash.Should().Equal(HMACSHA256.HashData(Convert.FromBase64String(EmailServices.RecipientHashKey), Encoding.UTF8.GetBytes(address)));
        row.RecipientHash.Should().NotEqual(SHA256.HashData(Encoding.UTF8.GetBytes(address)), "an unkeyed hash of an address can be reversed with a list of addresses");

        var stored = row.AsJson;
        foreach (var personal in new[] { address, address.Split('@')[0], Subject, Link, "code-AbC123", "Open" })
        {
            stored.Should().NotContain(personal);
        }
    }

    [Fact]
    public async Task DeliveriesTable_HasOnlyTheContractColumnsAsync()
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT column_name FROM information_schema.columns WHERE table_schema = 'email' AND table_name = 'deliveries' ORDER BY column_name",
            connection);
        var columns = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(CancellationToken))
        {
            while (await reader.ReadAsync(CancellationToken))
            {
                columns.Add(reader.GetString(0));
            }
        }

        columns.Should().Equal(
            ["created_at", "id", "org_id", "provider_message_id", "recipient_hash", "reference_id", "reference_version", "status", "template", "template_version", "updated_at"],
            "a new column could hold an address or content; the contract allows none (docs/contracts/phase-1.md, \"Data\")");
    }

    [Fact]
    public async Task Send_WhenMailpitIsDown_RecordsAFailedDeliveryAsync()
    {
        using var provider = Build(ClosedPort());
        var organizationId = Guid.CreateVersion7();

        var outcome = await provider.GetRequiredService<EmailDispatcher>().SendAsync(
            organizationId, s_template, EmailMessage.Create(EmailServices.NewAddress(), "Subject", "Body"), CancellationToken);

        outcome.Status.Should().Be(DeliveryStatus.Failed);
        outcome.Failure.Should().Be(EmailSendFailure.Unavailable);
        var row = await ReadDeliveryAsync(outcome.DeliveryId);
        row.Status.Should().Be(DeliveryStatus.Failed);
        row.ProviderMessageId.Should().BeNull();
    }

    [Fact]
    public async Task Deliveries_InAnotherOrganization_AreNotVisibleAsync()
    {
        using var provider = Build(mailpit.SmtpPort);
        var organizationId = Guid.CreateVersion7();
        await provider.GetRequiredService<EmailDispatcher>().SendAsync(
            organizationId, s_template, EmailMessage.Create(EmailServices.NewAddress(), "Subject", "Body"), CancellationToken);

        var own = await CountInOrganizationAsync(provider, organizationId);
        var other = await CountInOrganizationAsync(provider, Guid.CreateVersion7());

        own.Should().Be(1);
        other.Should().Be(0);
    }

    [Fact]
    public async Task Deliveries_AsTheApplicationRole_CannotBeDeletedAsync()
    {
        using var provider = Build(mailpit.SmtpPort);
        var organizationId = Guid.CreateVersion7();
        await provider.GetRequiredService<EmailDispatcher>().SendAsync(
            organizationId, s_template, EmailMessage.Create(EmailServices.NewAddress(), "Subject", "Body"), CancellationToken);

        var delete = () => InOrganizationAsync(provider, organizationId, async session =>
        {
            await using var command = new NpgsqlCommand("DELETE FROM email.deliveries", session.Connection, session.Transaction);
            return await command.ExecuteNonQueryAsync(CancellationToken);
        });

        (await delete.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task SendOnce_WhenTheConnectionBreaksBeforeTheReply_KeepsThePendingRowAndNeverSendsAgainAsync()
    {
        await using var server = new FakeSmtpServer(MailpitGreeting, fault: FakeSmtpFault.CloseBeforeDataReply);
        var organizationId = Guid.CreateVersion7();
        var reference = new EmailDeliveryReference(Guid.CreateVersion7(), 1);
        var address = EmailServices.NewAddress();
        using var broken = Build(server.Port, FakeSmtpServer.Host);

        var outcome = (await broken.GetRequiredService<EmailDispatcher>().SendOnceAsync(organizationId, s_template, reference, EmailMessage.Create(address, "Subject", "Body"), CancellationToken))!;

        outcome.Status.Should().Be(DeliveryStatus.Pending);
        outcome.Failure.Should().Be(EmailSendFailure.Unknown);
        (await ReadDeliveryAsync(outcome.DeliveryId)).Status.Should().Be(DeliveryStatus.Pending, "the server may have the message");
        using var working = Build(mailpit.SmtpPort);
        (await working.GetRequiredService<EmailDispatcher>().SendOnceAsync(organizationId, s_template, reference, EmailMessage.Create(address, "Subject", "Body"), CancellationToken))
            .Should().BeNull("a second send could deliver the message twice");
        (await mailpit.MessagesToAsync(address, CancellationToken)).Should().BeEmpty();
    }

    [Fact]
    public async Task SendOnce_WhenTheServerClosesAfterAccepting_RecordsSentAsync()
    {
        await using var server = new FakeSmtpServer(MailpitGreeting, fault: FakeSmtpFault.CloseAfterAccepting);
        using var provider = Build(server.Port, FakeSmtpServer.Host);

        var outcome = (await provider.GetRequiredService<EmailDispatcher>().SendOnceAsync(
            Guid.CreateVersion7(), s_template, new EmailDeliveryReference(Guid.CreateVersion7(), 1), EmailMessage.Create(EmailServices.NewAddress(), "Subject", "Body"), CancellationToken))!;

        outcome.Status.Should().Be(DeliveryStatus.Sent);
        (await ReadDeliveryAsync(outcome.DeliveryId)).Status.Should().Be(DeliveryStatus.Sent);
    }

    [Fact]
    public async Task SendOnce_CancelledBeforeTheHandover_RecordsFailedAndARedeliverySendsAsync()
    {
        await using var server = new FakeSmtpServer(MailpitGreeting, fault: FakeSmtpFault.StallOnEhlo);
        var organizationId = Guid.CreateVersion7();
        var reference = new EmailDeliveryReference(Guid.CreateVersion7(), 1);
        var address = EmailServices.NewAddress();
        using var stalled = Build(server.Port, FakeSmtpServer.Host);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        shutdown.CancelAfter(TimeSpan.FromMilliseconds(500));

        var send = () => stalled.GetRequiredService<EmailDispatcher>().SendOnceAsync(organizationId, s_template, reference, EmailMessage.Create(address, "Subject", "Body"), shutdown.Token);

        await send.Should().ThrowAsync<OperationCanceledException>("the consumer stops and the event goes back to its queue");
        (await CountWithStatusAsync(organizationId, DeliveryStatus.Failed)).Should().Be(1, "nothing was handed over, so the row must not block the redelivery");
        using var working = Build(mailpit.SmtpPort);
        var retried = await working.GetRequiredService<EmailDispatcher>().SendOnceAsync(organizationId, s_template, reference, EmailMessage.Create(address, "Subject", "Body"), CancellationToken);
        retried!.Status.Should().Be(DeliveryStatus.Sent);
        (await mailpit.MessagesToAsync(address, CancellationToken)).Should().ContainSingle();
    }

    private const string MailpitGreeting = "220 3f2a1b Mailpit ESMTP Service ready";

    private ServiceProvider Build(int mailpitPort, string? host = null) =>
        EmailServices.Build(
            "Development",
            options =>
            {
                options.Mailpit.Host = host ?? mailpit.SmtpHost;
                options.Mailpit.Port = mailpitPort;
            },
            database.ApplicationConnectionString());

    private async Task<long> CountWithStatusAsync(Guid organizationId, string status)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM email.deliveries WHERE org_id = $1 AND status = $2", connection);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = status });
        return (long)(await command.ExecuteScalarAsync(CancellationToken))!;
    }

    private static Task<long> CountInOrganizationAsync(ServiceProvider provider, Guid organizationId) =>
        InOrganizationAsync(provider, organizationId, async session =>
        {
            await using var command = new NpgsqlCommand("SELECT count(*) FROM email.deliveries", session.Connection, session.Transaction);
            return (long)(await command.ExecuteScalarAsync(CancellationToken))!;
        });

    private static async Task<T> InOrganizationAsync<T>(ServiceProvider provider, Guid organizationId, Func<DatabaseSession, Task<T>> action)
    {
        await using var scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<OrganizationContext>().Enter(organizationId);
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await unitOfWork.BeginAsync(CancellationToken);
        var result = await action(scope.ServiceProvider.GetRequiredService<DatabaseSession>());
        await unitOfWork.CommitAsync(CancellationToken);
        return result;
    }

    /// <summary>Reads the row as superuser (row-level security does not hide it), also as JSON with every column.</summary>
    private async Task<DeliveryRow> ReadDeliveryAsync(Guid deliveryId)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT org_id, template, template_version, recipient_hash, provider_message_id, status, row_to_json(d)::text FROM email.deliveries d WHERE id = $1",
            connection);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = deliveryId });
        await using var reader = await command.ExecuteReaderAsync(CancellationToken);
        (await reader.ReadAsync(CancellationToken)).Should().BeTrue();
        return new DeliveryRow(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetInt32(2),
            (byte[])reader.GetValue(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6));
    }

    private static int ClosedPort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed record DeliveryRow(Guid OrganizationId, string Template, int TemplateVersion, byte[] RecipientHash, string? ProviderMessageId, string Status, string AsJson);
}
