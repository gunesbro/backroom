using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Backroom.BuildingBlocks.Infrastructure.Maintenance;

/// <summary>
/// Stops the worker at startup when the runner's settings or a registered job's declaration are invalid,
/// or when a job is registered without the maintenance connection string. Messages never echo secrets.
/// </summary>
internal sealed partial class MaintenanceJobOptionsValidator(IEnumerable<IMaintenanceJob> jobs) : IValidateOptions<MaintenanceJobOptions>
{
    internal static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(10);

    internal static readonly TimeSpan MaxInterval = TimeSpan.FromDays(1);

    public ValidateOptionsResult Validate(string? name, MaintenanceJobOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        var registered = jobs.ToList();

        if (registered.Count > 0)
        {
            if (string.IsNullOrWhiteSpace(options.ConnectionString))
            {
                failures.Add("The maintenance connection string is missing; it is required when a maintenance job is registered.");
            }
            else
            {
                try
                {
                    _ = new NpgsqlConnectionStringBuilder(options.ConnectionString);
                }
                catch (ArgumentException)
                {
                    // The message could contain parts of the connection string, including the password.
                    failures.Add("The maintenance connection string is not valid.");
                }
            }
        }

        if (options.MaxPoolSize is < 1 or > 10)
        {
            failures.Add("MaintenanceJobs:MaxPoolSize must be between 1 and 10.");
        }

        if (options.MaxBatchesPerRun is < 1 or > 100)
        {
            failures.Add("MaintenanceJobs:MaxBatchesPerRun must be between 1 and 100.");
        }

        if (options.MaxStartDelay < TimeSpan.Zero || options.MaxStartDelay > TimeSpan.FromMinutes(10))
        {
            failures.Add("MaintenanceJobs:MaxStartDelay must be between 0 and 10 minutes.");
        }

        foreach (var job in registered)
        {
            if (job.Name is null || !JobNamePattern().IsMatch(job.Name))
            {
                failures.Add($"Maintenance job {job.GetType().Name} needs a name of the form module.job-name.");
            }

            if (job.Interval < MinInterval || job.Interval > MaxInterval)
            {
                failures.Add($"Maintenance job {job.Name} must run at an interval between 10 seconds and 1 day.");
            }

            if (job.BatchSize is < 1 or > MaintenanceJobOptions.MaxBatchSize)
            {
                failures.Add($"Maintenance job {job.Name} must declare a batch size between 1 and {MaintenanceJobOptions.MaxBatchSize}.");
            }
        }

        var duplicate = registered.GroupBy(job => job.Name, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            failures.Add($"More than one maintenance job is named {duplicate.Key}.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    // {module schema}.{kebab-case name}, like the module part of integration event types.
    [GeneratedRegex("^[a-z][a-z0-9_]{0,62}\\.[a-z][a-z0-9]*(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex JobNamePattern();
}
