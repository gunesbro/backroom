using AwesomeAssertions;
using Backroom.BuildingBlocks.Infrastructure.Maintenance;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Backroom.BuildingBlocks.UnitTests.Maintenance;

public sealed class MaintenanceJobOptionsValidatorTests
{
    private const string ConnectionString = "Host=localhost;Database=cell;Username=backroom_maintenance;Password=secret-value";

    [Fact]
    public void Validate_WithoutJobsAndWithoutConnectionString_Succeeds()
    {
        Validate([], new MaintenanceJobOptions()).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_WithAJobAndNoConnectionString_Fails()
    {
        var result = Validate([new TestJob("organization.invitation-expiry")], new MaintenanceJobOptions());

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("connection string is missing");
    }

    [Fact]
    public void Validate_WithAnInvalidConnectionString_FailsWithoutEchoingIt()
    {
        var result = Validate([new TestJob("organization.invitation-expiry")], new MaintenanceJobOptions { ConnectionString = "Host=x;Password=secret-value;Bogus Key=1" });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().NotContain("secret-value");
    }

    [Fact]
    public void Validate_WithValidSettingsAndJob_Succeeds()
    {
        Validate([new TestJob("organization.invitation-expiry")], new MaintenanceJobOptions { ConnectionString = ConnectionString }).Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("name")]
    [InlineData("Organization.Expiry")]
    [InlineData("organization.")]
    [InlineData("organization.a_b")]
    public void Validate_WithAnInvalidJobName_Fails(string name)
    {
        Validate([new TestJob(name)], new MaintenanceJobOptions { ConnectionString = ConnectionString }).Failed.Should().BeTrue();
    }

    [Theory]
    [InlineData(9)]
    [InlineData(86_401)]
    public void Validate_WithAnIntervalOutsideTenSecondsToOneDay_Fails(int seconds)
    {
        var job = new TestJob("organization.invitation-expiry") { Interval = TimeSpan.FromSeconds(seconds) };

        Validate([job], new MaintenanceJobOptions { ConnectionString = ConnectionString }).Failed.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public void Validate_WithABatchSizeOutsideTheLimits_Fails(int batchSize)
    {
        var job = new TestJob("organization.invitation-expiry") { BatchSize = batchSize };

        Validate([job], new MaintenanceJobOptions { ConnectionString = ConnectionString }).Failed.Should().BeTrue();
    }

    [Fact]
    public void Validate_WithTwoJobsOfTheSameName_Fails()
    {
        var result = Validate(
            [new TestJob("organization.invitation-expiry"), new TestJob("organization.invitation-expiry")],
            new MaintenanceJobOptions { ConnectionString = ConnectionString });

        result.Failed.Should().BeTrue();
    }

    [Theory]
    [InlineData("pool")]
    [InlineData("batches")]
    [InlineData("delay")]
    public void Validate_WithRunnerSettingsOutsideTheLimits_Fails(string setting)
    {
        var options = new MaintenanceJobOptions { ConnectionString = ConnectionString };
        switch (setting)
        {
            case "pool":
                options.MaxPoolSize = 11;
                break;
            case "batches":
                options.MaxBatchesPerRun = 0;
                break;
            default:
                options.MaxStartDelay = TimeSpan.FromHours(1);
                break;
        }

        Validate([new TestJob("organization.invitation-expiry")], options).Failed.Should().BeTrue();
    }

    [Fact]
    public void Options_WhenAJobIsRegisteredWithoutAConnectionString_FailWhenResolved()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMaintenanceJobRunner();
        services.AddMaintenanceJob<TestJob>();
        using var provider = services.BuildServiceProvider();

        var read = () => provider.GetRequiredService<IOptions<MaintenanceJobOptions>>().Value;

        read.Should().Throw<OptionsValidationException>().WithMessage("*connection string is missing*");
    }

    [Fact]
    public void Options_WhenNoJobIsRegistered_ResolveWithoutAConnectionString()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMaintenanceJobRunner();
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<MaintenanceJobOptions>>().Value.ConnectionString.Should().BeEmpty();
    }

    private static ValidateOptionsResult Validate(IEnumerable<IMaintenanceJob> jobs, MaintenanceJobOptions options) =>
        new MaintenanceJobOptionsValidator(jobs).Validate(null, options);

    private sealed class TestJob(string name) : IMaintenanceJob
    {
        public TestJob()
            : this("test.job")
        {
        }

        public string Name => name;

        public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(1);

        public int BatchSize { get; init; } = 100;

        public Task<int> RunBatchAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken) => Task.FromResult(0);
    }
}
