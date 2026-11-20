using AwesomeAssertions;
using Backroom.Modules.Email.Application.Sending;
using Backroom.Modules.Email.Infrastructure.Sending;

namespace Backroom.Modules.Email.IntegrationTests;

/// <summary>The greeting check and the message rules, without containers.</summary>
public sealed class MailpitGuardTests
{
    [Theory]
    [InlineData("220 70f91b23b57e Mailpit ESMTP Service ready\r\n")]
    [InlineData("220 mailpit Mailpit ESMTP Service ready\r\n")]
    [InlineData("220 localhost Mailpit ESMTP Service ready")]
    public void IsMailpitGreeting_ForMailpitsGreeting_IsTrue(string greeting)
    {
        MailpitGuard.IsMailpitGreeting(greeting).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("220 relay.local ESMTP Postfix\r\n")]
    [InlineData("220 smtp.example.com ESMTP Mailpit\r\n")]
    [InlineData("220 relay.local ESMTP Postfix Mailpit ESMTP Service ready\r\n")]
    [InlineData("220 relay Mailpit ESMTP Service ready\r\n250 extra\r\n")]
    [InlineData("554 mailpit Mailpit ESMTP Service ready\r\n")]
    [InlineData("220 mailpit mailpit esmtp service ready\r\n")]
    public void IsMailpitGreeting_ForAnyOtherGreeting_IsFalse(string? greeting)
    {
        MailpitGuard.IsMailpitGreeting(greeting).Should().BeFalse();
    }

    [Fact]
    public void IsMailpitGreeting_ForATooLongGreeting_IsFalse()
    {
        MailpitGuard.IsMailpitGreeting("220 " + new string('a', 600) + " Mailpit ESMTP Service ready\r\n").Should().BeFalse();
    }

    [Fact]
    public void GreetingRecorder_JoinsAGreetingSplitAcrossReads_AndIgnoresEverythingAfterTheClientSpeaks()
    {
        var recorder = new GreetingRecorder();
        recorder.LogServer("xx220 host Mail"u8.ToArray(), 2, 13);
        recorder.LogServer("pit ESMTP Service ready\r\n"u8.ToArray(), 0, 25);
        recorder.LogClient("EHLO me\r\n"u8.ToArray(), 0, 9);
        recorder.LogServer("250 recipient@example.test ok\r\n"u8.ToArray(), 0, 31);

        recorder.Greeting.Should().Be("220 host Mailpit ESMTP Service ready\r\n");
        MailpitGuard.IsMailpitGreeting(recorder.Greeting).Should().BeTrue();
    }

    [Fact]
    public void GreetingRecorder_ForAGreetingOverTheLimit_HasNoGreeting()
    {
        var recorder = new GreetingRecorder();
        var chunk = new byte[400];
        recorder.LogServer(chunk, 0, chunk.Length);
        recorder.LogServer(chunk, 0, chunk.Length);

        recorder.Greeting.Should().BeNull();
    }

    [Theory]
    [InlineData("Backroom <a@example.test>")]
    [InlineData("a@example.test, b@example.test")]
    [InlineData("a@example.test\r\nBcc: b@example.test")]
    [InlineData("not-an-address")]
    public void Create_WithAnInvalidRecipient_ThrowsWithoutTheAddress(string to)
    {
        var create = () => EmailMessage.Create(to, "Subject", "Body");

        create.Should().Throw<ArgumentException>().Which.Message.Should().NotContain("example.test");
    }

    [Fact]
    public void Create_NormalizesTheRecipient()
    {
        using var culture = Backroom.Testing.CultureScope.Turkish();

        EmailMessage.Create("  INVITED@Example.TEST ", "Subject", "Body").To.Should().Be("invited@example.test");
    }

    [Theory]
    [InlineData("Line\rbreak", "Linebreak")]
    [InlineData("Line\nbreak", "Linebreak")]
    public void Create_StripsLineBreaksFromTheSubject(string subject, string expected)
    {
        EmailMessage.Create("a@example.test", subject, "Body").Subject.Should().Be(expected);
    }

    [Theory]
    [InlineData(0x0B)]
    [InlineData(0x0C)]
    [InlineData(0x85)]
    [InlineData(0x2028)]
    [InlineData(0x2029)]
    public void Create_StripsUnicodeLineSeparatorsFromTheSubject(int separator)
    {
        EmailMessage.Create("a@example.test", "Line" + (char)separator + "break", "Body").Subject.Should().Be("Linebreak");
    }

    [Fact]
    public void Create_WithATooLongSubject_Throws()
    {
        var create = () => EmailMessage.Create("a@example.test", new string('s', EmailMessage.MaxSubjectLength + 1), "Body");

        create.Should().Throw<ArgumentException>();
    }
}
