using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.Modules.Email.Infrastructure;
using Backroom.Modules.Email.Infrastructure.Sending;

namespace Backroom.Modules.Email.IntegrationTests;

/// <summary>The email services as the cell's worker registers them, in a chosen environment.</summary>
internal static class EmailServices
{
    public const string FromAddress = "no-reply@backroom.test";

    /// <summary>A random key per test run.</summary>
    public static readonly string RecipientHashKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// The services in <paramref name="environment"/>. With <paramref name="environmentSetExplicitly"/> off, the host
    /// configuration has no environment name, as when DOTNET_ENVIRONMENT is unset (.NET then assumes Production).
    /// </summary>
    public static ServiceProvider Build(
        string environment,
        Action<EmailOptions> configure,
        string? databaseConnectionString = null,
        bool environmentSetExplicitly = true,
        ILoggerProvider? loggerProvider = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            if (loggerProvider is not null)
            {
                logging.AddProvider(loggerProvider);
            }
        });
        services.AddSingleton<IHostEnvironment>(new TestEnvironment(environment));
        var hostSettings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (environmentSetExplicitly)
        {
            hostSettings[HostDefaults.EnvironmentKey] = environment;
        }

        services.AddSingleton<IConfiguration>(new HostSettings(hostSettings));
        if (databaseConnectionString is not null)
        {
            services.AddDatabaseSession().Configure(options => options.ConnectionString = databaseConnectionString);
        }

        services.AddEmail().Configure(options =>
        {
            options.FromAddress = FromAddress;
            options.FromName = "Backroom";
            options.RecipientHashKey = RecipientHashKey;
            configure(options);
        });
        configureServices?.Invoke(services);
        return services.BuildServiceProvider();
    }

    /// <summary>What the host runs when it starts (the options registered with <c>ValidateOnStart</c>).</summary>
    public static void ValidateStartup(IServiceProvider provider) =>
        provider.GetRequiredService<IStartupValidator>().Validate();

    /// <summary>A unique <c>.test</c> address, so tests can share one Mailpit.</summary>
    public static string NewAddress() => $"person-{Guid.CreateVersion7():N}@example.test";
}
