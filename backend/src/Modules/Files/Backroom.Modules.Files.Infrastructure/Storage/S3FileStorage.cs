using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using Backroom.Modules.Files.Application.Storage;

namespace Backroom.Modules.Files.Infrastructure.Storage;

/// <summary>
/// <see cref="IFileStorage"/> over an S3-compatible store (SeaweedFS in development) with the AWS SDK for .NET. Calls
/// go to <see cref="S3StorageOptions.ServiceUrl"/> through <c>IHttpClientFactory</c>; signed URLs are made offline
/// for <see cref="S3StorageOptions.ClientUrl"/>, the endpoint clients reach. Neither the keys nor signed URLs are logged.
/// </summary>
internal sealed class S3FileStorage : IFileStorage, IDisposable
{
    /// <summary>The <c>IHttpClientFactory</c> client the SDK sends through.</summary>
    public const string HttpClientName = "Backroom.Files.Storage";

    private readonly AmazonS3Client _client;
    private readonly AmazonS3Client _signer;
    private readonly string _bucket;
    private readonly Protocol _clientProtocol;

    public S3FileStorage(IOptions<S3StorageOptions> options, IHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        var settings = options.Value;
        var credentials = new BasicAWSCredentials(settings.AccessKeyId, settings.SecretAccessKey);
        _bucket = settings.Bucket;
        _client = new AmazonS3Client(credentials, Config(settings, settings.ServiceUrl!, new SdkHttpClientFactory(httpClientFactory)));
        // Only signs (no request is sent), with the host clients use: the host is part of the signature.
        _signer = new AmazonS3Client(credentials, Config(settings, settings.ClientUrl, new SdkHttpClientFactory(httpClientFactory)));
        _clientProtocol = settings.ClientUrl.Scheme == Uri.UriSchemeHttps ? Protocol.HTTPS : Protocol.HTTP;
    }

    public async Task<PresignedUpload> CreateUploadAsync(string key, long size, string contentType, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _bucket,
            Key = key,
            Verb = HttpVerb.PUT,
            Expires = SdkExpiry(expiresAt),
            ContentType = contentType,
            Protocol = _clientProtocol,
        };
        // Signed headers: the upload must send them as they are. Content-Length holds the upload to the declared size
        // (HTTP clients set it from the body); If-None-Match makes the store refuse a second upload to the key, so the
        // object checked at completion can never be replaced through this URL.
        var length = size.ToString(System.Globalization.CultureInfo.InvariantCulture);
        request.Headers["Content-Length"] = length;
        request.Headers["If-None-Match"] = "*";
        var url = await _signer.GetPreSignedURLAsync(request);
        return new PresignedUpload(
            new Uri(url, UriKind.Absolute),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["Content-Type"] = contentType, ["If-None-Match"] = "*" });
    }

    public async Task<Uri> CreateDownloadUrlAsync(string key, string contentType, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _bucket,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = SdkExpiry(expiresAt),
            Protocol = _clientProtocol,
        };
        // Never inline (flow 6, step 5): the browser saves the file instead of rendering it. No file name: it would
        // appear in the URL.
        request.ResponseHeaderOverrides.ContentDisposition = "attachment";
        request.ResponseHeaderOverrides.ContentType = contentType;
        return new Uri(await _signer.GetPreSignedURLAsync(request), UriKind.Absolute);
    }

    public async Task<long?> GetSizeAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            var metadata = await _client.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = _bucket, Key = key }, cancellationToken);
            return metadata.ContentLength;
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<int> ReadStartAsync(string key, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        using var response = await _client.GetObjectAsync(
            new GetObjectRequest { BucketName = _bucket, Key = key, ByteRange = new ByteRange(0, buffer.Length - 1) },
            cancellationToken);
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await response.ResponseStream.ReadAsync(buffer[total..], cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken) =>
        await _client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _bucket, Key = key }, cancellationToken);

    /// <summary>Creates the bucket if it is missing (development only, <see cref="S3StorageOptions.CreateBucket"/>).</summary>
    internal async Task EnsureBucketAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _client.PutBucketAsync(new PutBucketRequest { BucketName = _bucket }, cancellationToken);
        }
        catch (AmazonS3Exception exception) when (exception.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists")
        {
        }
    }

    /// <summary>
    /// The SDK signs one second more than it is given (X-Amz-Expires counts from a signing time it rounds), so a
    /// 60-second URL would live 61; the tests pin X-Amz-Expires to the lifetime.
    /// </summary>
    private static DateTime SdkExpiry(DateTimeOffset expiresAt) => expiresAt.UtcDateTime.AddSeconds(-1);

    public void Dispose()
    {
        _client.Dispose();
        _signer.Dispose();
    }

    private static AmazonS3Config Config(S3StorageOptions settings, Uri endpoint, HttpClientFactory httpClientFactory) => new()
    {
        ServiceURL = endpoint.ToString(),
        AuthenticationRegion = settings.Region,
        // Bucket in the path: S3-compatible stores and local hosts have no per-bucket host names.
        ForcePathStyle = true,
        Timeout = settings.Timeout,
        MaxErrorRetry = 2,
        // Checksums only where the S3 API requires them: compatible stores do not all support the newer defaults, and
        // presigned uploads cannot carry them.
        RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
        ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        HttpClientFactory = httpClientFactory,
    };
}
