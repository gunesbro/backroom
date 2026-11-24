using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Backroom.Modules.Files.Infrastructure.Storage;

/// <summary>
/// Creates the cell's bucket at startup when <see cref="S3StorageOptions.CreateBucket"/> is on (development only,
/// checked by the validator). A failure is logged and does not stop the cell: uploads fail until the store is reachable.
/// </summary>
internal sealed partial class DevelopmentBucketCreator(
    S3FileStorage storage,
    IOptions<S3StorageOptions> options,
    ILogger<DevelopmentBucketCreator> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.CreateBucket)
        {
            return;
        }

        try
        {
            await storage.EnsureBucketAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogFailed(logger, exception.GetType().Name);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "The development bucket could not be created ({ExceptionType}); uploads fail until object storage is reachable.")]
    private static partial void LogFailed(ILogger logger, string exceptionType);
}
