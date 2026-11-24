using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Backroom.Modules.Identity.Application;

namespace Backroom.Modules.Identity.Infrastructure;

/// <summary>
/// Runs <see cref="RevokedSessionCleanup"/> in the control plane host, like Directory's invitation jobs: shortly after start
/// (a random delay, so instances started together do not run in step), then every <see cref="Interval"/>. Every instance runs
/// it; the job skips rows another instance holds. A failing run is logged and tried again at the next interval.
/// </summary>
internal sealed partial class RevokedSessionCleanupWorker(RevokedSessionCleanup cleanup, TimeProvider timeProvider, ILogger<RevokedSessionCleanupWorker> logger) : BackgroundService
{
    /// <summary>Rows are kept five and a half minutes; a run every minute keeps the table at a few minutes of logouts.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    public static readonly TimeSpan MaxStartDelay = TimeSpan.FromSeconds(30);

    [SuppressMessage("Design", "CA1031", Justification = "The worker must survive outages; a failed run is logged and tried again at the next interval.")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(RandomNumberGenerator.GetInt32((int)MaxStartDelay.TotalMilliseconds)), timeProvider, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await cleanup.RunAsync(stoppingToken);
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    LogFailed(logger, exception);
                }

                await Task.Delay(Interval, timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
    }

    [LoggerMessage(EventId = 11, Level = LogLevel.Warning, Message = "The session revocation cleanup failed; trying again at the next interval.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
