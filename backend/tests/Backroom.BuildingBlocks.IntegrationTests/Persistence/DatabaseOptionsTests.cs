using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Backroom.BuildingBlocks.Infrastructure.Persistence;

namespace Backroom.BuildingBlocks.IntegrationTests.Persistence;

public sealed class DatabaseOptionsTests
{
    [Theory]
    [InlineData("", 10, "*connection string is missing*")]
    [InlineData("Host=localhost;Username=backroom_app;Password=secret-value;NotAKeyword=1", 10, "*connection string is not valid*")]
    [InlineData("Host=localhost;Username=backroom_app", 0, "*MaxPoolSize*")]
    [InlineData("Host=localhost;Username=backroom_app", 201, "*MaxPoolSize*")]
    public void Options_WhenInvalid_FailValidation(string connectionString, int maxPoolSize, string message)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDatabaseSession().Configure(options =>
        {
            options.ConnectionString = connectionString;
            options.MaxPoolSize = maxPoolSize;
        });
        using var provider = services.BuildServiceProvider();

        var read = () => provider.GetRequiredService<IOptions<DatabaseOptions>>().Value;

        var failure = read.Should().Throw<OptionsValidationException>().WithMessage(message).Which;
        failure.Message.Should().NotContain("secret-value");
    }
}
