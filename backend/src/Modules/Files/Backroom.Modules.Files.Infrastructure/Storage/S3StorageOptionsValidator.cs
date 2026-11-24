using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Backroom.Modules.Files.Infrastructure.Storage;

/// <summary>
/// Refuses to start without a complete storage configuration, and outside Development without HTTPS endpoints or
/// with bucket creation on (insecure defaults must not reach production). Messages never contain the keys.
/// </summary>
internal sealed partial class S3StorageOptionsValidator(IHostEnvironment environment) : IValidateOptions<S3StorageOptions>
{
    public ValidateOptionsResult Validate(string? name, S3StorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        var development = environment.IsDevelopment();

        CheckEndpoint(options.ServiceUrl, "ServiceUrl", required: true, development, failures);
        CheckEndpoint(options.PublicUrl, "PublicUrl", required: false, development, failures);

        if (!BucketName().IsMatch(options.Bucket) || options.Bucket.Contains("..", StringComparison.Ordinal))
        {
            failures.Add("Files:Storage:Bucket must be an S3 bucket name: 3 to 63 lower-case letters, digits, dots and hyphens.");
        }

        if (string.IsNullOrWhiteSpace(options.AccessKeyId) || string.IsNullOrWhiteSpace(options.SecretAccessKey))
        {
            failures.Add("Files:Storage:AccessKeyId and Files:Storage:SecretAccessKey are required.");
        }
        else if (options.SecretAccessKey.Length < 16)
        {
            failures.Add("Files:Storage:SecretAccessKey must be at least 16 characters.");
        }

        if (string.IsNullOrWhiteSpace(options.Region))
        {
            failures.Add("Files:Storage:Region is required.");
        }

        if (options.CreateBucket && !development)
        {
            failures.Add("Files:Storage:CreateBucket is for development only; deployments provision their buckets.");
        }

        if (options.MaxRetries is < 0 or > 3)
        {
            failures.Add("Files:Storage:MaxRetries must be between 0 and 3.");
        }

        if (options.Timeout <= TimeSpan.Zero || options.Timeout > TimeSpan.FromSeconds(30))
        {
            failures.Add("Files:Storage:Timeout must be between 0 and 30 seconds.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void CheckEndpoint(Uri? url, string key, bool required, bool development, List<string> failures)
    {
        if (url is null)
        {
            if (required)
            {
                failures.Add($"Files:Storage:{key} is required.");
            }

            return;
        }

        if (!url.IsAbsoluteUri || !string.IsNullOrEmpty(url.UserInfo) || !string.IsNullOrEmpty(url.Query) || !string.IsNullOrEmpty(url.Fragment))
        {
            failures.Add($"Files:Storage:{key} must be an absolute URL without credentials, query or fragment.");
            return;
        }

        // Signed URLs work like short-lived credentials: plain HTTP only in development.
        if (url.Scheme != Uri.UriSchemeHttps && !(development && url.Scheme == Uri.UriSchemeHttp))
        {
            failures.Add($"Files:Storage:{key} must use https (http is allowed in development only).");
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$", RegexOptions.CultureInvariant)]
    private static partial Regex BucketName();
}
