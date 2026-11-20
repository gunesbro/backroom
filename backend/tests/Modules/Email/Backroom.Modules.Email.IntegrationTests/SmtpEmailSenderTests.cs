using System.Net;
using System.Net.Sockets;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Backroom.Modules.Email.Application.Sending;
using Backroom.Testing.Mailpit;

namespace Backroom.Modules.Email.IntegrationTests;

/// <summary>Sending through a real Mailpit, and the guard against servers that are not Mailpit (docs/adr/0008).</summary>
[Trait("Category", "Privacy")]
public sealed class SmtpEmailSenderTests(MailpitServer mailpit)
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Send_InDevelopment_ArrivesInMailpitAsync()
    {
        using var provider = EmailServices.Build("Development", options =>
        {
            options.Mailpit.Host = mailpit.SmtpHost;
            options.Mailpit.Port = mailpit.SmtpPort;
        });
        var address = EmailServices.NewAddress();
        var message = EmailMessage.Create(address, "Join Acme on Backroom", "Open the link to join.", "<p>Open the link to join.</p>", senderName: "Acme on Backroom");

        var result = await provider.GetRequiredService<IEmailSender>().SendAsync(message, CancellationToken);

        result.Succeeded.Should().BeTrue();
        var received = (await mailpit.MessagesToAsync(address, CancellationToken)).Should().ContainSingle().Subject;
        received.Subject.Should().Be("Join Acme on Backroom");
        received.MessageId.Should().Be(result.ProviderMessageId);
        using var details = await mailpit.MessageAsync(received.Id, CancellationToken);
        var from = details.RootElement.GetProperty("From");
        from.GetProperty("Address").GetString().Should().Be(EmailServices.FromAddress);
        from.GetProperty("Name").GetString().Should().Be("Acme on Backroom");
        details.RootElement.GetProperty("Text").GetString().Should().Contain("Open the link to join.");
        details.RootElement.GetProperty("HTML").GetString().Should().Contain("<p>Open the link to join.</p>");
    }

    [Fact]
    public async Task Send_WithLineBreaksInTheSubject_SendsOneHeaderLineAsync()
    {
        using var provider = EmailServices.Build("Development", options =>
        {
            options.Mailpit.Host = mailpit.SmtpHost;
            options.Mailpit.Port = mailpit.SmtpPort;
        });
        var address = EmailServices.NewAddress();
        var injected = EmailServices.NewAddress();
        var message = EmailMessage.Create(address, "Hello\r\nBcc: " + injected, "Body", senderName: "Acme\r\nBcc: " + injected);

        (await provider.GetRequiredService<IEmailSender>().SendAsync(message, CancellationToken)).Succeeded.Should().BeTrue();

        (await mailpit.MessagesToAsync(address, CancellationToken)).Should().ContainSingle();
        (await mailpit.MessagesToAsync(injected, CancellationToken)).Should().BeEmpty();
    }

    [Fact]
    public async Task Send_ToALocalServerThatIsNotMailpit_SendsNothingAsync()
    {
        // A mail relay on the developer's machine would pass the host check; its greeting gives it away.
        await using var relay = new FakeSmtpServer();
        using var provider = EmailServices.Build("Development", options =>
        {
            options.Mailpit.Host = FakeSmtpServer.Host;
            options.Mailpit.Port = relay.Port;
        });
        EmailServices.ValidateStartup(provider);

        var result = await provider.GetRequiredService<IEmailSender>().SendAsync(EmailMessage.Create(EmailServices.NewAddress(), "Subject", "Body"), CancellationToken);

        result.Failure.Should().Be(EmailSendFailure.NotMailpit);
        relay.Connections.Should().Be(1);
        relay.Commands.Intersect(["MAIL", "RCPT", "DATA", "AUTH"]).Should().BeEmpty();
    }

    [Fact]
    public async Task Send_ToAServerThatOnlyMentionsMailpit_SendsNothingAsync()
    {
        await using var relay = new FakeSmtpServer("220 relay.local ESMTP Postfix (mailpit compatible) Mailpit ESMTP Service ready");
        using var provider = EmailServices.Build("Development", options =>
        {
            options.Mailpit.Host = FakeSmtpServer.Host;
            options.Mailpit.Port = relay.Port;
        });

        var result = await provider.GetRequiredService<IEmailSender>().SendAsync(EmailMessage.Create(EmailServices.NewAddress(), "Subject", "Body"), CancellationToken);

        result.Failure.Should().Be(EmailSendFailure.NotMailpit);
        relay.Commands.Should().NotContain("MAIL");
    }

    [Fact]
    public async Task Send_WhenMailpitIsDown_FailsWithoutReroutingAsync()
    {
        // Production without the allow setting: the real server is configured but must never be used as a fallback.
        await using var real = new FakeSmtpServer("220 smtp.example.com ESMTP");
        using var provider = EmailServices.Build("Production", options =>
        {
            options.Mailpit.Host = "127.0.0.1";
            options.Mailpit.Port = ClosedPort();
            options.Smtp.Host = FakeSmtpServer.Host;
            options.Smtp.Port = real.Port;
        });
        EmailServices.ValidateStartup(provider);

        var result = await provider.GetRequiredService<IEmailSender>().SendAsync(EmailMessage.Create(EmailServices.NewAddress(), "Subject", "Body"), CancellationToken);

        result.Failure.Should().Be(EmailSendFailure.Unavailable);
        real.Connections.Should().Be(0);
    }

    [Fact]
    public async Task Send_InProductionWithoutTheAllowSetting_GoesToMailpitNotTheRealServerAsync()
    {
        await using var real = new FakeSmtpServer("220 smtp.example.com ESMTP");
        using var provider = EmailServices.Build("Production", options =>
        {
            options.AllowRealSending = false;
            options.Mailpit.Host = mailpit.SmtpHost;
            options.Mailpit.Port = mailpit.SmtpPort;
            options.Smtp.Host = FakeSmtpServer.Host;
            options.Smtp.Port = real.Port;
        });
        EmailServices.ValidateStartup(provider);
        var address = EmailServices.NewAddress();

        var result = await provider.GetRequiredService<IEmailSender>().SendAsync(EmailMessage.Create(address, "Subject", "Body"), CancellationToken);

        result.Succeeded.Should().BeTrue();
        (await mailpit.MessagesToAsync(address, CancellationToken)).Should().ContainSingle();
        real.Connections.Should().Be(0, "real sending needs both Production and the allow setting");
    }

    [Fact]
    public async Task Send_ToARealServerWithoutStartTls_SendsNoCredentialsAndNoMessageAsync()
    {
        // The fake server does not offer STARTTLS; MailKit must not fall back to plain text.
        await using var real = new FakeSmtpServer("220 smtp.example.com ESMTP");
        using var provider = EmailServices.Build("Production", options =>
        {
            options.AllowRealSending = true;
            options.Smtp.Host = FakeSmtpServer.Host;
            options.Smtp.Port = real.Port;
            options.Smtp.Security = Infrastructure.Sending.SmtpSecurity.StartTls;
            options.Smtp.UserName = "sender";
            options.Smtp.Password = "not-a-real-password";
        });
        EmailServices.ValidateStartup(provider);

        var result = await provider.GetRequiredService<IEmailSender>().SendAsync(EmailMessage.Create(EmailServices.NewAddress(), "Subject", "Body"), CancellationToken);

        result.Failure.Should().Be(EmailSendFailure.Unavailable);
        real.Connections.Should().Be(1);
        real.Commands.Intersect(["AUTH", "MAIL", "RCPT", "DATA"]).Should().BeEmpty();
    }

    [Fact]
    public async Task Send_WithMailpitsGreetingSplitAcrossReads_IsRecognizedAsync()
    {
        const string Greeting = "220 3f2a1b Mailpit ESMTP Service ready";
        await using var server = new FakeSmtpServer(Greeting, splitGreetingAt: Greeting.IndexOf("Mailpit", StringComparison.Ordinal) + 3);
        using var provider = EmailServices.Build("Development", options =>
        {
            options.Mailpit.Host = FakeSmtpServer.Host;
            options.Mailpit.Port = server.Port;
        });

        var result = await provider.GetRequiredService<IEmailSender>().SendAsync(EmailMessage.Create(EmailServices.NewAddress(), "Subject", "Body"), CancellationToken);

        result.Succeeded.Should().BeTrue();
        server.Commands.Should().Contain(["MAIL", "RCPT", "DATA"]);
    }

    [Fact]
    public async Task Send_WhenTheServerClosesTheConnectionAfterAccepting_IsSentAsync()
    {
        await using var server = new FakeSmtpServer(MailpitGreeting, fault: FakeSmtpFault.CloseAfterAccepting);
        using var provider = BuildFor(server);

        var result = await provider.GetRequiredService<IEmailSender>().SendAsync(EmailMessage.Create(EmailServices.NewAddress(), "Subject", "Body"), CancellationToken);

        result.Succeeded.Should().BeTrue("the server accepted the message; a failed QUIT must not make it look unsent and be sent again");
        result.ProviderMessageId.Should().NotBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Send_WhenTheConnectionBreaksBeforeTheReplyToTheMessage_IsUnknownAsync(bool duringTheReply)
    {
        await using var server = new FakeSmtpServer(MailpitGreeting, fault: duringTheReply ? FakeSmtpFault.CloseDuringDataReply : FakeSmtpFault.CloseBeforeDataReply);
        using var provider = BuildFor(server);

        var result = await provider.GetRequiredService<IEmailSender>().SendAsync(EmailMessage.Create(EmailServices.NewAddress(), "Subject", "Body"), CancellationToken);

        result.Failure.Should().Be(EmailSendFailure.Unknown, "the server may have accepted it, so it must not be retried");
        server.Commands.Should().Contain("DATA");
    }

    [Fact]
    public async Task Send_CancelledBeforeTheMessageIsHandedOver_IsCancelledAsync()
    {
        await using var server = new FakeSmtpServer(MailpitGreeting, fault: FakeSmtpFault.StallOnEhlo);
        using var provider = BuildFor(server);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        shutdown.CancelAfter(TimeSpan.FromMilliseconds(500));

        var result = await provider.GetRequiredService<IEmailSender>().SendAsync(EmailMessage.Create(EmailServices.NewAddress(), "Subject", "Body"), shutdown.Token);

        result.Failure.Should().Be(EmailSendFailure.Cancelled, "nothing was handed over, so a redelivery may send it");
        server.Commands.Should().NotContain("DATA");
    }

    private const string MailpitGreeting = "220 3f2a1b Mailpit ESMTP Service ready";

    private static ServiceProvider BuildFor(FakeSmtpServer server) =>
        EmailServices.Build("Development", options =>
        {
            options.Mailpit.Host = FakeSmtpServer.Host;
            options.Mailpit.Port = server.Port;
        });

    private static int ClosedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
