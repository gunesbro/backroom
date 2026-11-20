using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Backroom.Modules.Email.IntegrationTests;

/// <summary>A host environment with the given name, so tests can run the email services as Development or Production.</summary>
internal sealed class TestEnvironment(string environmentName) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = environmentName;

    public string ApplicationName { get; set; } = "Backroom.Cell.Host";

    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
