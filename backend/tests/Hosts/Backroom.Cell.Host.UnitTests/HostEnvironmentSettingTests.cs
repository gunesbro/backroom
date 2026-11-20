using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;

namespace Backroom.Cell.Host.UnitTests;

/// <summary>
/// The Email module refuses real sending unless the environment name was set explicitly, which it reads from the
/// host setting <see cref="HostDefaults.EnvironmentKey"/>. This pins that the cell's builder exposes that setting
/// when it is given, so the check cannot silently stop seeing it.
/// </summary>
public sealed class HostEnvironmentSettingTests
{
    [Fact]
    public void Configuration_WhenTheEnvironmentIsGiven_HasTheEnvironmentSetting()
    {
        var builder = WebApplication.CreateBuilder(["--environment", "Staging"]);

        builder.Configuration[HostDefaults.EnvironmentKey].Should().Be("Staging");
        builder.Environment.EnvironmentName.Should().Be("Staging");
    }
}
