using AwesomeAssertions;
using Backroom.BuildingBlocks.Infrastructure.Messaging;

namespace Backroom.BuildingBlocks.UnitTests.Messaging;

public sealed class MessagingOptionsValidatorTests
{
    private readonly MessagingOptionsValidator _validator = new();

    [Theory]
    [InlineData("amqp://user:pass@localhost:5672/", true)]
    [InlineData("amqps://user:pass@broker.example.test/vhost", true)]
    [InlineData("http://localhost:5672/", false)]
    [InlineData("", false)]
    public void Validate_RabbitMqConnectionString_AcceptsOnlyAmqpUris(string connectionString, bool valid)
    {
        var result = _validator.Validate(null, new RabbitMqOptions { ConnectionString = connectionString });

        result.Succeeded.Should().Be(valid);
        if (!valid)
        {
            result.FailureMessage.Should().NotContain("pass");
        }
    }

    [Fact]
    public void Validate_RelayWithValidSettings_Succeeds()
    {
        _validator.Validate(null, ValidRelayOptions()).Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("schemas")]
    [InlineData("schema-name")]
    [InlineData("batch")]
    [InlineData("poll")]
    [InlineData("pool")]
    [InlineData("connection")]
    public void Validate_RelayWithInvalidSetting_Fails(string invalid)
    {
        var options = ValidRelayOptions();
        switch (invalid)
        {
            case "schemas":
                options.Schemas.Clear();
                break;
            case "schema-name":
                options.Schemas.Add("Organization; DROP");
                break;
            case "batch":
                options.BatchSize = OutboxRelayOptions.MaxBatchSize + 1;
                break;
            case "poll":
                options.PollInterval = TimeSpan.Zero;
                break;
            case "pool":
                options.MaxPoolSize = 0;
                break;
            default:
                options.ConnectionString = string.Empty;
                break;
        }

        _validator.Validate(null, options).Failed.Should().BeTrue();
    }

    [Fact]
    public void Validate_ConsumersWithDefaults_Succeeds()
    {
        _validator.Validate(null, new IntegrationEventConsumerOptions()).Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(16, 0)]
    [InlineData(16, 7_200_000)]
    public void Validate_ConsumersWithInvalidSetting_Fails(int prefetch, int delayMilliseconds)
    {
        var options = new IntegrationEventConsumerOptions { Prefetch = (ushort)prefetch };
        options.RetryDelays.Add(TimeSpan.FromMilliseconds(delayMilliseconds));

        _validator.Validate(null, options).Failed.Should().BeTrue();
    }

    [Fact]
    public void Validate_ConsumersWithRepeatedDelay_Fails()
    {
        var options = new IntegrationEventConsumerOptions();
        options.RetryDelays.Add(options.RetryDelays[0]);

        _validator.Validate(null, options).Failed.Should().BeTrue();
    }

    [Fact]
    public void Validate_RelayWithOnlyInboxSchemas_Succeeds()
    {
        var options = new OutboxRelayOptions { ConnectionString = "Host=localhost;Username=backroom_relay" };
        options.InboxSchemas.Add("control_plane_connector");

        _validator.Validate(null, options).Succeeded.Should().BeTrue();
    }

    private static OutboxRelayOptions ValidRelayOptions()
    {
        var options = new OutboxRelayOptions { ConnectionString = "Host=localhost;Username=backroom_relay" };
        options.Schemas.Add("organization");
        return options;
    }
}
