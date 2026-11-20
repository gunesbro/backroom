using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Backroom.Modules.Organization.Infrastructure;
using Backroom.Modules.Organization.Infrastructure.Invitations;

namespace Backroom.Modules.Organization.IntegrationTests;

/// <summary>The web origin invitation links point to: one plain origin, https outside local development.</summary>
public sealed class InvitationLinkOptionsTests
{
    [Theory]
    [InlineData("https://app.example.test", false)]
    [InlineData("https://app.example.test/", false)]
    [InlineData("https://app.example.test:8443", false)]
    [InlineData("http://localhost:5173", true)]
    [InlineData("http://127.0.0.1:5173", true)]
    public void Check_APlainOrigin_Passes(string origin, bool isDevelopment) =>
        InvitationLinkOptionsValidator.TryCheck(new Uri(origin), isDevelopment, out _).Should().BeTrue();

    [Theory]
    [InlineData("http://localhost:5173", false)]
    [InlineData("http://app.example.test", true)]
    [InlineData("https://app.example.test/invite", false)]
    [InlineData("https://app.example.test/?next=x", false)]
    [InlineData("https://app.example.test/#x", false)]
    [InlineData("https://user:secret@app.example.test", false)]
    [InlineData("ftp://app.example.test", false)]
    [InlineData("javascript:alert(1)", true)]
    public void Check_AnythingElse_Fails(string origin, bool isDevelopment)
    {
        InvitationLinkOptionsValidator.TryCheck(new Uri(origin), isDevelopment, out var error).Should().BeFalse();
        error.Should().StartWith(InvitationLinkOptions.SectionName).And.NotContain("secret");
    }

    [Fact]
    public void Check_NoOrigin_Fails() =>
        InvitationLinkOptionsValidator.TryCheck(null, isDevelopment: true, out _).Should().BeFalse();

    [Theory]
    [InlineData(null)]
    [InlineData("http://localhost:5173")]
    public void Startup_InProductionWithoutAnHttpsOrigin_Fails(string? origin)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHostEnvironment>(new Production());
        services.AddOrganizationInvitationLinks().Configure(options => options.WebOrigin = origin is null ? null : new Uri(origin));
        using var provider = services.BuildServiceProvider();

        var start = () => provider.GetRequiredService<IStartupValidator>().Validate();

        start.Should().Throw<OptionsValidationException>();
    }

    private sealed class Production : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;

        public string ApplicationName { get; set; } = "tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
