using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Backroom.BuildingBlocks.Application.Organizations;
using Backroom.BuildingBlocks.Application.Persistence;
using Backroom.Modules.Email.Application.Deliveries;
using Backroom.Modules.Email.Application.Invitations;
using Backroom.Modules.Email.Application.Templates;
using Backroom.Modules.Organization.Contracts;
using Backroom.Testing.Mailpit;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.Email.IntegrationTests;

/// <summary>
/// The invitation email (docs/contracts/phase-1.md, flow 3, step 3, and "Invitation security", Spam and phishing),
/// consumed from <c>organization.invitation-ready.v1</c> in the event's unit of work, as the worker's consumer runs
/// it, and read back from a real Mailpit.
/// </summary>
[Trait("Category", "Privacy")]
public sealed partial class InvitationEmailTests(PostgresDatabase database, MailpitServer mailpit) : IAsyncLifetime
{
    private readonly FakeInvitationLinks _links = new();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await EmailSchema.MigrateOnceAsync(database, CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Ready_SendsTheInvitationWithItsLinkAsTheOnlyLinkAsync()
    {
        using var provider = Build(mailpit.SmtpPort);
        var ready = NewReady();
        var address = EmailServices.NewAddress();
        var link = _links.Add(ready, address, "Acme");

        await ConsumeAsync(provider, ready);

        using var message = await SingleMessageToAsync(address);
        var root = message.RootElement;
        root.GetProperty("From").GetProperty("Name").GetString().Should().Be("Acme on Backroom");
        root.GetProperty("From").GetProperty("Address").GetString().Should().Be(EmailServices.FromAddress);
        root.GetProperty("To").EnumerateArray().Select(to => to.GetProperty("Address").GetString()).Should().Equal(address);
        root.GetProperty("Cc").GetArrayLength().Should().Be(0);
        root.GetProperty("Bcc").GetArrayLength().Should().Be(0);
        root.GetProperty("Subject").GetString().Should().Be("You are invited to join Acme on Backroom");

        var text = root.GetProperty("Text").GetString()!;
        var html = root.GetProperty("HTML").GetString()!;
        Occurrences(text, link).Should().Be(1);
        UrlPattern().Matches(text).Select(match => match.Value).Should().Equal([link], "the invitation link is the only link");
        HrefPattern().Matches(html).Select(match => match.Groups[1].Value).Should().Equal([link], "the button is the only anchor");
        UrlPattern().Matches(html).Select(match => match.Value).Should().AllBe(link);
        text.Should().Contain("If you did not expect this invitation").And.Contain("never ask for your password");
        html.Should().Contain("If you did not expect this invitation");
    }

    [Fact]
    public async Task Ready_WithAHostileOrganizationName_EncodesItKeepsHeadersOnOneLineAndMakesNoLinkAsync()
    {
        using var provider = Build(mailpit.SmtpPort);
        var ready = NewReady();
        var address = EmailServices.NewAddress();
        const string Name = "<script>alert(1)</script>\r\nBcc: victim@example.test ‮evil.example";
        _links.Add(ready, address, Name);

        await ConsumeAsync(provider, ready);

        using var message = await SingleMessageToAsync(address);
        var root = message.RootElement;
        root.GetProperty("Bcc").GetArrayLength().Should().Be(0, "a line break in the name cannot add a header");
        root.GetProperty("To").GetArrayLength().Should().Be(1);
        (await mailpit.MessagesToAsync("victim@example.test", CancellationToken)).Should().BeEmpty();
        var subject = root.GetProperty("Subject").GetString()!;
        subject.Should().NotContain("\r").And.NotContain("\n").And.NotContain("‮", "a direction override could disguise the name");
        root.GetProperty("From").GetProperty("Name").GetString().Should().NotContain("\n").And.NotContain("@", "a name must not pass for an address");
        subject.Should().NotContain("@").And.NotContain("evil.example", "a domain in the name is broken up in headers too");

        var html = root.GetProperty("HTML").GetString()!;
        html.Should().NotContain("<script").And.Contain("&lt;script&gt;");
        html.Should().NotContain("evil.example", "a domain in the name is broken up so clients do not link it");
        root.GetProperty("Text").GetString().Should().NotContain("evil.example");
        HrefPattern().Matches(html).Should().ContainSingle();
    }

    [Fact]
    public void Create_WithALongOrganizationName_CutsIt()
    {
        var name = new string('a', 200);

        var message = InvitationEmail.Create(new InvitationLink(new Uri("https://app.backroom.test/invite#code"), EmailServices.NewAddress(), name), "en-US");

        message.SenderName.Should().Be(new string('a', EmailText.MaxNameLength - 1) + "… on Backroom");
        message.Subject.Should().Contain(new string('a', EmailText.MaxNameLength - 1) + "…").And.NotContain(new string('a', EmailText.MaxNameLength));
    }

    [Fact]
    public async Task Ready_Redelivered_SendsOneEmailAsync()
    {
        using var provider = Build(mailpit.SmtpPort);
        var ready = NewReady();
        var address = EmailServices.NewAddress();
        _links.Add(ready, address, "Acme");

        // The first handling's transaction (with its inbox row) is lost after the send, so the event comes again.
        await ConsumeAsync(provider, ready, commit: false);
        await ConsumeAsync(provider, ready);

        (await mailpit.MessagesToAsync(address, CancellationToken)).Should().ContainSingle();
        (await DeliveriesAsync(ready)).Select(row => row.Status).Should().Equal(DeliveryStatus.Sent);
    }

    [Fact]
    public async Task Ready_ForAVersionAResendReplaced_SendsNothingAsync()
    {
        using var provider = Build(mailpit.SmtpPort);
        var ready = NewReady();
        var address = EmailServices.NewAddress();
        _links.Add(ready with { CodeVersion = 2 }, address, "Acme");

        await ConsumeAsync(provider, ready);

        (await mailpit.MessagesToAsync(address, CancellationToken)).Should().BeEmpty();
        (await DeliveriesAsync(ready)).Should().BeEmpty();
    }

    [Fact]
    public async Task Ready_WithAPendingDeliveryForTheVersion_DoesNotResendAsync()
    {
        using var provider = Build(mailpit.SmtpPort);
        var ready = NewReady();
        var address = EmailServices.NewAddress();
        _links.Add(ready, address, "Acme");
        await SeedPendingAsync(ready);

        await ConsumeAsync(provider, ready);

        (await mailpit.MessagesToAsync(address, CancellationToken)).Should().BeEmpty("an earlier send's outcome is unknown, so it is never resent");
        (await DeliveriesAsync(ready)).Select(row => row.Status).Should().Equal(DeliveryStatus.Pending);
    }

    [Fact]
    public async Task Ready_WhenMailpitIsDown_FailsForARetry_AndTheRetrySendsAsync()
    {
        var ready = NewReady();
        var address = EmailServices.NewAddress();
        _links.Add(ready, address, "Acme");

        using (var down = Build(ClosedPort()))
        {
            var consume = () => ConsumeAsync(down, ready);
            await consume.Should().ThrowAsync<InvalidOperationException>("the consumer retries the event later");
        }

        using var up = Build(mailpit.SmtpPort);
        await ConsumeAsync(up, ready);

        (await mailpit.MessagesToAsync(address, CancellationToken)).Should().ContainSingle();
        (await DeliveriesAsync(ready)).Select(row => row.Status).Should().BeEquivalentTo([DeliveryStatus.Failed, DeliveryStatus.Sent]);
    }

    [Fact]
    public async Task Ready_RecordsADeliveryWithIdsOnlyAsync()
    {
        using var provider = Build(mailpit.SmtpPort);
        var ready = NewReady();
        var address = EmailServices.NewAddress();
        var link = _links.Add(ready, address, "Zebra Widgets");

        await ConsumeAsync(provider, ready);

        var row = (await DeliveriesAsync(ready)).Should().ContainSingle().Subject;
        row.Json.Should().Contain("\"template\":\"invitation\"").And.Contain("\"template_version\":1").And.Contain("\"reference_version\":1");
        foreach (var personal in new[] { address, address.Split('@')[0], link, link[(link.IndexOf('#', StringComparison.Ordinal) + 1)..], "Zebra", "Backroom" })
        {
            row.Json.Should().NotContain(personal);
        }
    }

    [Theory]
    [InlineData("tr-TR", "Backroom: Acme çalışma alanına davet edildin", "Acme (Backroom)")]
    [InlineData("tr", "Backroom: Acme çalışma alanına davet edildin", "Acme (Backroom)")]
    [InlineData("en-US", "You are invited to join Acme on Backroom", "Acme on Backroom")]
    [InlineData("de-DE", "You are invited to join Acme on Backroom", "Acme on Backroom")]
    [InlineData(null, "You are invited to join Acme on Backroom", "Acme on Backroom")]
    public void Create_InEachLanguage_UsesItsTextsWithEnglishAsTheFallback(string? locale, string subject, string senderName)
    {
        var message = InvitationEmail.Create(new InvitationLink(new Uri("https://app.backroom.test/invite#code"), EmailServices.NewAddress(), "Acme"), locale);

        message.Subject.Should().Be(subject);
        message.SenderName.Should().Be(senderName);
        message.HtmlBody.Should().Contain("href=\"https://app.backroom.test/invite#code\"");
        message.TextBody.Should().Contain("https://app.backroom.test/invite#code");
    }

    [Fact]
    public void CleanName_PlaceholdersInTheName_AreNotExpanded() =>
        EmailText.Fill("{organization} on {product}", EmailText.CleanName("{product}", "x")).Should().Be("{product} on Backroom");

    [Theory]
    [InlineData("می‌خواهم", "می‌خواهم")]
    [InlineData("👩‍👩‍👧", "👩‍👩‍👧")]
    [InlineData("‍Acme‌", "Acme")]
    [InlineData("Ac​me ‍ Co", "Acme Co")]
    [InlineData("Acme­﻿", "Acme")]
    public void CleanName_KeepsJoinersBetweenLettersAndDropsOtherInvisibleCharacters(string name, string expected) =>
        EmailText.CleanName(name, "x").Should().Be(expected);

    [Theory]
    [InlineData("en-US", "Invitation on Backroom", "You are invited to join your team on Backroom")]
    [InlineData("tr-TR", "Backroom daveti", "Backroom: ekibinin çalışma alanına davet edildin")]
    public void Create_WithNothingVisibleInTheName_UsesTheFallbackTexts(string locale, string senderName, string subject)
    {
        var message = InvitationEmail.Create(new InvitationLink(new Uri("https://app.backroom.test/invite#code"), EmailServices.NewAddress(), "‮​ @ "), locale);

        message.SenderName.Should().Be(senderName);
        message.Subject.Should().Be(subject);
    }

    [Fact]
    public void Create_WithAnAddressAsTheName_KeepsItFromLookingLikeOneInTheHeaders()
    {
        var message = InvitationEmail.Create(new InvitationLink(new Uri("https://app.backroom.test/invite#code"), EmailServices.NewAddress(), "security@bank.example"), "en-US");

        message.SenderName.Should().NotContain("@").And.NotContain("bank.example").And.StartWith("security bank.");
        message.Subject.Should().NotContain("@").And.NotContain("bank.example");
    }

    [Fact]
    public async Task Ready_WhenTheConnectionBreaksBeforeTheReply_IsNotRetriedAndNeverSentAgainAsync()
    {
        var ready = NewReady();
        var address = EmailServices.NewAddress();
        _links.Add(ready, address, "Acme");
        await using (var server = new FakeSmtpServer("220 3f2a1b Mailpit ESMTP Service ready", fault: FakeSmtpFault.CloseBeforeDataReply))
        {
            using var broken = Build(server.Port, FakeSmtpServer.Host);
            await ConsumeAsync(broken, ready);
        }

        // A later redelivery (or a duplicate publish) finds the pending row.
        using var working = Build(mailpit.SmtpPort);
        await ConsumeAsync(working, ready, commit: false);

        (await mailpit.MessagesToAsync(address, CancellationToken)).Should().BeEmpty("the first send may have arrived; a second invitation must not");
        (await DeliveriesAsync(ready)).Select(row => row.Status).Should().Equal(DeliveryStatus.Pending);
    }

    private ServiceProvider Build(int mailpitPort, string? host = null) =>
        EmailServices.Build(
            "Development",
            options =>
            {
                options.Mailpit.Host = host ?? mailpit.SmtpHost;
                options.Mailpit.Port = mailpitPort;
            },
            database.ApplicationConnectionString(),
            configureServices: services => services.AddSingleton<IInvitationLinks>(_links));

    private static InvitationReady NewReady() =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), DateTimeOffset.UtcNow, Guid.CreateVersion7(), 1, InvitationReady.DefaultLocale);

    /// <summary>As the worker's consumer runs a handler: a unit of work in the event's organization, committed afterwards.</summary>
    private static async Task ConsumeAsync(ServiceProvider provider, InvitationReady ready, bool commit = true)
    {
        await using var scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<OrganizationContext>().Enter(ready.OrgId);
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await unitOfWork.BeginAsync(CancellationToken);
        await scope.ServiceProvider.GetRequiredService<InvitationReadyConsumer>().HandleAsync(ready, CancellationToken);
        if (commit)
        {
            await unitOfWork.CommitAsync(CancellationToken);
        }
    }

    private async Task<JsonDocument> SingleMessageToAsync(string address)
    {
        var listed = (await mailpit.MessagesToAsync(address, CancellationToken)).Should().ContainSingle().Subject;
        return await mailpit.MessageAsync(listed.Id, CancellationToken);
    }

    /// <summary>The deliveries for the event's invitation and version, read as superuser, each also as JSON with every column.</summary>
    private async Task<List<(string Status, string Json)>> DeliveriesAsync(InvitationReady ready)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT status, row_to_json(d)::text FROM email.deliveries d WHERE org_id = $1 AND reference_id = $2 AND reference_version = $3 ORDER BY created_at",
            connection);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = ready.OrgId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = ready.InvitationId });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = ready.CodeVersion });
        await using var reader = await command.ExecuteReaderAsync(CancellationToken);
        var rows = new List<(string, string)>();
        while (await reader.ReadAsync(CancellationToken))
        {
            rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        return rows;
    }

    private async Task SeedPendingAsync(InvitationReady ready)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO email.deliveries (id, org_id, template, template_version, recipient_hash, status, created_at, updated_at, reference_id, reference_version)
            VALUES (uuidv7(), $1, 'invitation', 1, $2, 'pending', now(), now(), $3, $4)
            """,
            connection);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = ready.OrgId });
        command.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = RandomNumberGenerator.GetBytes(32) });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = ready.InvitationId });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = ready.CodeVersion });
        await command.ExecuteNonQueryAsync(CancellationToken);
    }

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static int ClosedPort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [GeneratedRegex("https?://[^\\s\"<>]+")]
    private static partial Regex UrlPattern();

    [GeneratedRegex("href=\"([^\"]*)\"")]
    private static partial Regex HrefPattern();

    /// <summary>Organization's links as a test sets them: one per invitation and code version.</summary>
    private sealed class FakeInvitationLinks : IInvitationLinks
    {
        private readonly ConcurrentDictionary<(Guid, int), InvitationLink> _links = new();

        /// <summary>A link with a fresh 43-character code for that version; returns its text.</summary>
        public string Add(InvitationReady ready, string email, string organizationName)
        {
            var code = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var url = new Uri("https://app.backroom.test/invite#" + code);
            _links[(ready.InvitationId, ready.CodeVersion)] = new InvitationLink(url, email, organizationName);
            return url.AbsoluteUri;
        }

        public Task<string?> GetCodeHashAsync(Guid invitationId, int codeVersion, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<InvitationLink?> GetLinkAsync(Guid invitationId, int codeVersion, CancellationToken cancellationToken) =>
            Task.FromResult(_links.TryGetValue((invitationId, codeVersion), out var link) ? link : null);
    }
}
