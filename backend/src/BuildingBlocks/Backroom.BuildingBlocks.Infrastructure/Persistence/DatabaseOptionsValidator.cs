using Microsoft.Extensions.Options;
using Npgsql;

namespace Backroom.BuildingBlocks.Infrastructure.Persistence;

/// <summary>Stops the service at startup when the database settings are missing or out of range.</summary>
internal sealed class DatabaseOptionsValidator : IValidateOptions<DatabaseOptions>
{
    public ValidateOptionsResult Validate(string? name, DatabaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            failures.Add("The database connection string is missing.");
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
                failures.Add("The database connection string is not valid.");
            }
        }

        if (options.MaxPoolSize is < 1 or > DatabaseOptions.MaxPoolSizeLimit)
        {
            failures.Add($"Database:MaxPoolSize must be between 1 and {DatabaseOptions.MaxPoolSizeLimit}.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
