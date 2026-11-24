using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.Modules.ControlPlaneConnector.Infrastructure;
using Backroom.Modules.Organization.Infrastructure;
using Backroom.Modules.Organization.Infrastructure.Invitations;
using Backroom.Modules.Permissions.Infrastructure;
using Backroom.Modules.ControlPlaneConnector.Infrastructure.Registration;

namespace Backroom.Modules.CellManagement.IntegrationTests;

/// <summary>The connector's start-up checks; no control plane or Keycloak is needed.</summary>
public sealed class ControlPlaneConnectorStartupTests : IDisposable
{
    private readonly string _keyDirectory = Path.Combine(Path.GetTempPath(), "backroom-cell-tests", Guid.CreateVersion7().ToString("N"));

    public void Dispose()
    {
        if (System.IO.Directory.Exists(_keyDirectory))
        {
            System.IO.Directory.Delete(_keyDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData("Production", "http://cp.example.test", false)]
    [InlineData("Production", "https://cp.example.test", true)]
    [InlineData("Development", "http://localhost:5000", true)]
    public void Options_ControlPlaneUrlScheme_IsHttpsOutsideDevelopment(string environment, string controlPlaneUrl, bool valid)
    {
        using var host = BuildHost(environment, controlPlaneUrl, registrationKey: "key");

        var read = () => host.Services.GetRequiredService<IOptions<ControlPlaneConnectorOptions>>().Value;

        if (valid)
        {
            read.Should().NotThrow();
        }
        else
        {
            read.Should().Throw<OptionsValidationException>().WithMessage("*https*");
        }
    }

    [Fact]
    public async Task Worker_WhenNotRegisteredAndKeyIsMissing_StopsInsteadOfRetryingAsync()
    {
        using var host = BuildHost("Development", "http://localhost:5000", registrationKey: null);
        var worker = host.Services.GetServices<IHostedService>().OfType<CellRegistrationWorker>().Single();

        await worker.StartAsync(TestContext.Current.CancellationToken);

        // ExecuteAsync runs in the background; it must end with the error rather than keep retrying.
        var execute = () => worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await execute.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public void Options_WithoutControlPlaneUrl_AreInvalid()
    {
        using var host = BuildHost("Development", controlPlaneUrl: null, registrationKey: "key");

        var read = () => host.Services.GetRequiredService<IOptions<ControlPlaneConnectorOptions>>().Value;

        read.Should().Throw<OptionsValidationException>().WithMessage("*ControlPlaneUrl is required*");
    }

    private IHost BuildHost(string environment, string? controlPlaneUrl, string? registrationKey)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = environment });
        builder.Configuration["Cell:Id"] = "cell-test";
        builder.Configuration["Authentication:Authority"] = "https://keycloak.example.test/realms/backroom";
        builder.Configuration["ControlPlaneConnector:ControlPlaneUrl"] = controlPlaneUrl;
        builder.Configuration["ControlPlaneConnector:RegistrationKey"] = registrationKey;
        builder.Configuration["ControlPlaneConnector:KeyDirectory"] = _keyDirectory;
        // The link lease reads the cell database's connection string; nothing connects here.
        builder.Configuration["Database:ConnectionString"] = "Host=localhost;Database=unused";
        builder.Configuration["OutboxRelay:ConnectionString"] = "Host=localhost;Database=unused";
        builder.Services.AddDatabaseSession().BindConfiguration(DatabaseOptions.SectionName);
        // The modules the connector hands messages to, as the cell host adds them.
        builder.Services.AddPermissions();
        builder.Services.AddOrganization();
        builder.Configuration[$"{InvitationKeyOptions.SectionName}:CurrentVersion"] = "1";
        builder.Configuration[$"{InvitationKeyOptions.SectionName}:Keys:1"] = Convert.ToBase64String(new byte[32]);
        builder.Services.AddOrganizationInvitations().BindConfiguration(InvitationKeyOptions.SectionName);
        // Realtime applies the revoked sessions; its Valkey-backed implementation is not needed to start the connector.
        builder.Services.AddSingleton<Backroom.Modules.Realtime.Contracts.ISessionRevocation, RecordingSessionRevocation>();
        builder.AddControlPlaneConnector();
        return builder.Build();
    }
}
