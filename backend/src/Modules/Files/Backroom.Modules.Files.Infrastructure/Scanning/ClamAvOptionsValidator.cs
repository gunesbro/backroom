using Microsoft.Extensions.Options;

namespace Backroom.Modules.Files.Infrastructure.Scanning;

/// <summary>A missing or unreasonable scanner setting stops the service at startup instead of leaving files unscanned.</summary>
internal sealed class ClamAvOptionsValidator : IValidateOptions<ClamAvOptions>
{
    public ValidateOptionsResult Validate(string? name, ClamAvOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(options.Host))
        {
            failures.Add("Files:Scanner:Host is required.");
        }

        if (options.Port is < 1 or > 65535)
        {
            failures.Add("Files:Scanner:Port must be between 1 and 65535.");
        }

        if (options.ConnectTimeout < TimeSpan.FromMilliseconds(100) || options.ConnectTimeout > TimeSpan.FromSeconds(30))
        {
            failures.Add("Files:Scanner:ConnectTimeout must be between 100 ms and 30 s.");
        }

        if (options.ScanTimeout < TimeSpan.FromSeconds(1) || options.ScanTimeout > TimeSpan.FromSeconds(50))
        {
            failures.Add("Files:Scanner:ScanTimeout must be between 1 s and 50 s (the maintenance connection ends after 60 idle seconds).");
        }

        if (options.ChunkSize is < 1024 or > 1024 * 1024)
        {
            failures.Add("Files:Scanner:ChunkSize must be between 1 KiB and 1 MiB.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
