using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Backroom.Modules.Email.Infrastructure.Sending;

namespace Backroom.Modules.Email.IntegrationTests;

/// <summary>The startup checks of docs/adr/0008: outside Production the worker does not start unless the target is Mailpit.</summary>
[Trait("Category", "Privacy")]
public sealed class EmailStartupTests
{
    [Theory]
    [InlineData("localhost")]
    [InlineData("LOCALHOST")]
    [InlineData("127.0.0.1")]
    [InlineData("127.10.0.1")]
    [InlineData("::1")]
    [InlineData("[::1]")]
    [InlineData("mailpit")]
    public void Start_InDevelopmentWithAMailpitHost_Succeeds(string host)
    {
        using var provider = EmailServices.Build("Development", options => options.Mailpit.Host = host);

        var start = () => EmailServices.ValidateStartup(provider);

        start.Should().NotThrow();
    }

    [Theory]
    [InlineData("smtp.example.com")]
    [InlineData("mailpit.example.com")]
    [InlineData("mailpit.")]
    [InlineData("my-mailpit")]
    [InlineData("10.0.0.5")]
    [InlineData("0.0.0.0")]
    [InlineData("localhost.example.com")]
    [InlineData(" localhost")]
    [InlineData("")]
    public void Start_InDevelopmentWithAnotherHost_Fails(string host)
    {
        using var provider = EmailServices.Build("Development", options => options.Mailpit.Host = host);

        var start = () => EmailServices.ValidateStartup(provider);

        start.Should().Throw<OptionsValidationException>().Which.Message.Should().Contain("Email:Mailpit:Host");
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Staging")]
    [InlineData("Test")]
    public void Start_OutsideProductionWithARealServerConfigured_Fails(string environment)
    {
        using var provider = EmailServices.Build(environment, options =>
        {
            options.Mailpit.Host = "localhost";
            options.Smtp.Host = "smtp.example.com";
        });

        var start = () => EmailServices.ValidateStartup(provider);

        start.Should().Throw<OptionsValidationException>().Which.Message.Should().Contain("Email:Smtp must not be configured");
    }

    [Fact]
    public void Start_OutsideProductionWithRealServerCredentialsOnly_Fails()
    {
        using var provider = EmailServices.Build("Development", options =>
        {
            options.Mailpit.Host = "localhost";
            options.Smtp.Password = "not-a-real-password";
        });

        var start = () => EmailServices.ValidateStartup(provider);

        var failure = start.Should().Throw<OptionsValidationException>().Which.Message;
        failure.Should().Contain("Email:Smtp must not be configured");
        failure.Should().NotContain("not-a-real-password", "a secret's value is never put in a message");
    }

    [Fact]
    public void Start_OutsideProductionWithRealSendingAllowed_Fails()
    {
        using var provider = EmailServices.Build("Development", options =>
        {
            options.Mailpit.Host = "localhost";
            options.AllowRealSending = true;
        });

        var start = () => EmailServices.ValidateStartup(provider);

        start.Should().Throw<OptionsValidationException>().Which.Message.Should().Contain("Email:AllowRealSending is allowed only in Production");
    }

    [Fact]
    public void Start_InProductionWithoutTheAllowSetting_StillRequiresMailpit()
    {
        using var provider = EmailServices.Build("Production", options =>
        {
            options.Mailpit.Host = "smtp.example.com";
            options.Smtp.Host = "smtp.example.com";
        });

        var start = () => EmailServices.ValidateStartup(provider);

        start.Should().Throw<OptionsValidationException>().Which.Message.Should().Contain("Email:Mailpit:Host");
    }

    [Fact]
    public void Start_InProductionWithRealSendingAndTls_Succeeds()
    {
        using var provider = EmailServices.Build("Production", options =>
        {
            options.AllowRealSending = true;
            options.Smtp.Host = "smtp.example.com";
            options.Smtp.Security = SmtpSecurity.Tls;
            options.Smtp.Port = 465;
        });

        var start = () => EmailServices.ValidateStartup(provider);

        start.Should().NotThrow();
    }

    [Fact]
    public void Start_WithRealSendingAndNoExplicitEnvironment_Fails()
    {
        // .NET reports Production when DOTNET_ENVIRONMENT is unset; that must not count as the first switch.
        using var provider = EmailServices.Build(
            "Production",
            options =>
            {
                options.AllowRealSending = true;
                options.Smtp.Host = "smtp.example.com";
            },
            environmentSetExplicitly: false);

        var start = () => EmailServices.ValidateStartup(provider);

        start.Should().Throw<OptionsValidationException>().Which.Message.Should().Contain("environment set explicitly");
    }

    [Fact]
    public void Start_WithoutRealSendingAndNoExplicitEnvironment_SucceedsWithMailpit()
    {
        using var provider = EmailServices.Build("Production", options => options.Mailpit.Host = "mailpit", environmentSetExplicitly: false);

        var start = () => EmailServices.ValidateStartup(provider);

        start.Should().NotThrow();
    }

    [Fact]
    public async Task StartupReport_InProductionWithARealServerButNoAllowSetting_WarnsAsync()
    {
        var logs = new RecordingLoggerProvider();
        using var provider = EmailServices.Build(
            "Production",
            options =>
            {
                options.Mailpit.Host = "mailpit";
                options.Smtp.Host = "smtp.example.com";
                options.Smtp.UserName = "sender";
                options.Smtp.Password = "not-a-real-password";
            },
            loggerProvider: logs);

        foreach (var service in provider.GetServices<IHostedService>())
        {
            await service.StartAsync(TestContext.Current.CancellationToken);
        }

        logs.Entries.Should().Contain(entry => entry.Level == LogLevel.Warning && entry.Message.Contains("Email:AllowRealSending is off", StringComparison.Ordinal));
        logs.Entries.Should().NotContain(entry => entry.Message.Contains("not-a-real-password", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartupReport_WithoutARealServer_DoesNotWarnAsync()
    {
        var logs = new RecordingLoggerProvider();
        using var provider = EmailServices.Build("Development", options => options.Mailpit.Host = "localhost", loggerProvider: logs);

        foreach (var service in provider.GetServices<IHostedService>())
        {
            await service.StartAsync(TestContext.Current.CancellationToken);
        }

        logs.Entries.Should().NotContain(entry => entry.Level >= LogLevel.Warning);
        logs.Entries.Should().Contain(entry => entry.Message.Contains("Mailpit at localhost", StringComparison.Ordinal));
    }

    [Fact]
    public void Start_InProductionWithRealSendingAndPlainText_Fails()
    {
        using var provider = EmailServices.Build("Production", options =>
        {
            options.AllowRealSending = true;
            options.Smtp.Host = "smtp.example.com";
            options.Smtp.Security = (SmtpSecurity)42;
        });

        var start = () => EmailServices.ValidateStartup(provider);

        start.Should().Throw<OptionsValidationException>().Which.Message.Should().Contain("Email:Smtp:Security");
    }

    [Fact]
    public void Start_InProductionWithRealSendingAndAUserWithoutPassword_Fails()
    {
        using var provider = EmailServices.Build("Production", options =>
        {
            options.AllowRealSending = true;
            options.Smtp.Host = "smtp.example.com";
            options.Smtp.UserName = "sender";
        });

        var start = () => EmailServices.ValidateStartup(provider);

        start.Should().Throw<OptionsValidationException>().Which.Message.Should().Contain("set together");
    }

    [Theory]
    [InlineData("")]
    [InlineData("c2hvcnQ=")]
    [InlineData("not base64!")]
    public void Start_WithAMissingOrWeakRecipientHashKey_Fails(string key)
    {
        using var provider = EmailServices.Build("Development", options =>
        {
            options.Mailpit.Host = "localhost";
            options.RecipientHashKey = key;
        });

        var start = () => EmailServices.ValidateStartup(provider);

        start.Should().Throw<OptionsValidationException>().Which.Message.Should().Contain("Email:RecipientHashKey");
    }

    [Theory]
    [InlineData("")]
    [InlineData("Backroom <no-reply@backroom.test>")]
    [InlineData("a@b.test, c@d.test")]
    public void Start_WithAnInvalidSenderAddress_Fails(string address)
    {
        using var provider = EmailServices.Build("Development", options =>
        {
            options.Mailpit.Host = "localhost";
            options.FromAddress = address;
        });

        var start = () => EmailServices.ValidateStartup(provider);

        start.Should().Throw<OptionsValidationException>().Which.Message.Should().Contain("Email:FromAddress");
    }

    [Fact]
    public void Start_WithASenderNameOnTwoLines_Fails()
    {
        using var provider = EmailServices.Build("Development", options =>
        {
            options.Mailpit.Host = "localhost";
            options.FromName = "Backroom\r\nBcc: someone@example.test";
        });

        var start = () => EmailServices.ValidateStartup(provider);

        start.Should().Throw<OptionsValidationException>().Which.Message.Should().Contain("Email:FromName");
    }

    [Theory]
    [InlineData("Production", true, true)]
    [InlineData("Production", false, false)]
    [InlineData("Development", true, false)]
    [InlineData("Development", false, false)]
    [InlineData("Staging", true, false)]
    public void Resolve_NeedsProductionAndTheAllowSetting_ForTheRealServer(string environment, bool allow, bool expectReal)
    {
        var options = new EmailOptions { AllowRealSending = allow };
        options.Mailpit.Host = "localhost";
        options.Smtp.Host = "smtp.example.com";

        var transport = EmailTransport.Resolve(options, isProduction: environment == "Production");

        (transport.Mode == EmailTransportMode.Real).Should().Be(expectReal);
        transport.Host.Should().Be(expectReal ? "smtp.example.com" : "localhost");
    }

    [Fact]
    public void Transport_WhenPrinted_DoesNotShowThePassword()
    {
        var options = new EmailOptions { AllowRealSending = true };
        options.Smtp.Host = "smtp.example.com";
        options.Smtp.UserName = "sender";
        options.Smtp.Password = "not-a-real-password";

        EmailTransport.Resolve(options, isProduction: true).ToString().Should().NotContain("not-a-real-password");
    }
}
