using Microsoft.Extensions.Options;
using Npgsql;
using Backroom.BuildingBlocks.Infrastructure.Persistence;

namespace Backroom.BuildingBlocks.Infrastructure.Messaging;

/// <summary>Stops the service at startup when messaging settings are missing or out of range. Messages never echo secrets.</summary>
internal sealed class MessagingOptionsValidator
    : IValidateOptions<RabbitMqOptions>, IValidateOptions<OutboxRelayOptions>, IValidateOptions<IntegrationEventConsumerOptions>
{
    public ValidateOptionsResult Validate(string? name, IntegrationEventConsumerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();

        if (options.Prefetch is < 1 or > 1000)
        {
            failures.Add("IntegrationEventConsumers:Prefetch must be between 1 and 1000.");
        }

        if (options.RetryDelays.Count > IntegrationEventConsumerOptions.MaxRetries)
        {
            failures.Add($"IntegrationEventConsumers:RetryDelays may list at most {IntegrationEventConsumerOptions.MaxRetries} delays.");
        }

        if (options.RetryDelays.Any(delay => delay < TimeSpan.FromMilliseconds(10) || delay > TimeSpan.FromHours(1)))
        {
            failures.Add("IntegrationEventConsumers:RetryDelays must each be between 10 ms and 1 hour.");
        }

        if (options.RequeuePause < TimeSpan.FromMilliseconds(10) || options.RequeuePause > TimeSpan.FromSeconds(30))
        {
            failures.Add("IntegrationEventConsumers:RequeuePause must be between 10 ms and 30 seconds.");
        }

        if (options.RetryDelays.Distinct().Count() != options.RetryDelays.Count)
        {
            // Each delay has its own retry queue, named after it.
            failures.Add("IntegrationEventConsumers:RetryDelays must not repeat a delay.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    public ValidateOptionsResult Validate(string? name, RabbitMqOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Uri.TryCreate(options.ConnectionString, UriKind.Absolute, out var uri)
            || (uri.Scheme is not ("amqp" or "amqps")))
        {
            return ValidateOptionsResult.Fail("The RabbitMQ connection string is missing or is not an amqp:// or amqps:// URI.");
        }

        return string.IsNullOrWhiteSpace(options.ClientName)
            ? ValidateOptionsResult.Fail("RabbitMq:ClientName is required.")
            : ValidateOptionsResult.Success;
    }

    public ValidateOptionsResult Validate(string? name, OutboxRelayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            failures.Add("The outbox relay connection string is missing.");
        }
        else
        {
            try
            {
                _ = new NpgsqlConnectionStringBuilder(options.ConnectionString);
            }
            catch (ArgumentException)
            {
                failures.Add("The outbox relay connection string is not valid.");
            }
        }

        if (options.Schemas.Count == 0 && options.InboxSchemas.Count == 0)
        {
            failures.Add("OutboxRelay:Schemas or OutboxRelay:InboxSchemas must list at least one module schema.");
        }

        if (options.Schemas.Distinct(StringComparer.Ordinal).Count() != options.Schemas.Count
            || options.InboxSchemas.Distinct(StringComparer.Ordinal).Count() != options.InboxSchemas.Count)
        {
            failures.Add("OutboxRelay:Schemas or OutboxRelay:InboxSchemas lists a schema more than once.");
        }

        if (options.InboxRetention < TimeSpan.FromDays(1))
        {
            failures.Add("OutboxRelay:InboxRetention must be at least one day, longer than any redelivery.");
        }

        foreach (var schema in options.Schemas.Concat(options.InboxSchemas))
        {
            try
            {
                OrganizationRowLevelSecurity.ValidateIdentifier(schema, nameof(options.Schemas));
            }
            catch (ArgumentException exception)
            {
                failures.Add(exception.Message);
            }
        }

        if (options.MaxPoolSize is < 1 or > 10)
        {
            failures.Add("OutboxRelay:MaxPoolSize must be between 1 and 10.");
        }

        if (options.BatchSize is < 1 or > OutboxRelayOptions.MaxBatchSize)
        {
            failures.Add($"OutboxRelay:BatchSize must be between 1 and {OutboxRelayOptions.MaxBatchSize}.");
        }

        if (options.PollInterval < TimeSpan.FromMilliseconds(50) || options.PollInterval > TimeSpan.FromMinutes(1))
        {
            failures.Add("OutboxRelay:PollInterval must be between 50 ms and 1 minute.");
        }

        if (options.PublishedRetention < TimeSpan.Zero || options.CleanupInterval < TimeSpan.FromSeconds(1))
        {
            failures.Add("OutboxRelay:PublishedRetention must not be negative and CleanupInterval must be at least 1 second.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
