namespace Backroom.Modules.Files.Infrastructure.Storage;

/// <summary>
/// The cell's S3-compatible object storage (configuration section <c>Files:Storage</c>), checked at startup by
/// <see cref="S3StorageOptionsValidator"/>. The keys come from secrets (Aspire parameters in development) and are never logged.
/// </summary>
public sealed class S3StorageOptions
{
    public const string SectionName = "Files:Storage";

    /// <summary>The endpoint the cell calls (inside the deployment's network).</summary>
    public Uri? ServiceUrl { get; set; }

    /// <summary>The endpoint clients upload to and download from, if it differs from <see cref="ServiceUrl"/>; signed URLs name it.</summary>
    public Uri? PublicUrl { get; set; }

    /// <summary>The cell's bucket; each cell has its own.</summary>
    public string Bucket { get; set; } = string.Empty;

    public string AccessKeyId { get; set; } = string.Empty;

    public string SecretAccessKey { get; set; } = string.Empty;

    /// <summary>The signing region; S3-compatible stores usually accept any, the default is the S3 default.</summary>
    public string Region { get; set; } = "us-east-1";

    /// <summary>Creates the bucket at startup if it is missing. Development only; deployments provision their buckets.</summary>
    public bool CreateBucket { get; set; }

    /// <summary>The longest the cell waits for one storage call attempt.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Retries of a failed storage call; few, because a completion holds a database connection meanwhile.</summary>
    public int MaxRetries { get; set; } = 1;

    /// <summary>The endpoint signed URLs name.</summary>
    internal Uri ClientUrl => PublicUrl ?? ServiceUrl ?? throw new InvalidOperationException("Files:Storage:ServiceUrl is required.");
}
