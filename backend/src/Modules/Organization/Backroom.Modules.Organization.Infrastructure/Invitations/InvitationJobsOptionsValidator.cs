using Microsoft.Extensions.Options;

namespace Backroom.Modules.Organization.Infrastructure.Invitations;

/// <summary>Stops the worker at startup when the invitation job settings are missing or outside their limits.</summary>
internal sealed class InvitationJobsOptionsValidator : IValidateOptions<InvitationJobsOptions>
{
    public ValidateOptionsResult Validate(string? name, InvitationJobsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.RelayConnectionString))
        {
            return ValidateOptionsResult.Fail("OutboxRelay:ConnectionString (the relay role) is required for the invitation jobs.");
        }

        if (options.Interval < InvitationJobsOptions.MinInterval || options.Interval > InvitationJobsOptions.MaxInterval)
        {
            return ValidateOptionsResult.Fail($"{InvitationJobsOptions.SectionName}:Interval must be between {InvitationJobsOptions.MinInterval} and {InvitationJobsOptions.MaxInterval}.");
        }

        if (options.BatchSize is < 1 or > InvitationJobsOptions.MaxBatchSize)
        {
            return ValidateOptionsResult.Fail($"{InvitationJobsOptions.SectionName}:BatchSize must be between 1 and {InvitationJobsOptions.MaxBatchSize}.");
        }

        if (options.MaxBatchesPerRun is < 1 or > InvitationJobsOptions.MaxBatchesLimit)
        {
            return ValidateOptionsResult.Fail($"{InvitationJobsOptions.SectionName}:MaxBatchesPerRun must be between 1 and {InvitationJobsOptions.MaxBatchesLimit}.");
        }

        return ValidateOptionsResult.Success;
    }
}
