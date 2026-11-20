using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Backroom.Modules.Organization.Infrastructure.Invitations;

/// <summary>
/// Runs <see cref="InvitationJobs"/> in the cell's worker role: shortly after start (a random delay of up to
/// <see cref="MaxStartDelay"/>, so instances started together do not run in step), then every configured interval.
/// Every instance runs them; the jobs skip rows another instance holds. A failing job is logged and does not keep
/// the other one from running.
/// </summary>
internal sealed partial class InvitationJobsWorker(
    InvitationJobs jobs,
    IOptions<InvitationJobsOptions> options,
    TimeProvider timeProvider,
    ILogger<InvitationJobsWorker> logger) : BackgroundService
{
    public static readonly TimeSpan MaxStartDelay = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(RandomNumberGenerator.GetInt32((int)MaxStartDelay.TotalMilliseconds)), timeProvider, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await RunAsync("expiry", jobs.ExpireAsync, stoppingToken);
                await RunAsync("retention", jobs.ApplyRetentionAsync, stoppingToken);
                await Task.Delay(options.Value.Interval, timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
    }

    [SuppressMessage("Design", "CA1031", Justification = "The worker must survive outages; a failed job is logged and tried again at the next interval.")]
    private async Task RunAsync(string job, Func<CancellationToken, Task<int>> run, CancellationToken stoppingToken)
    {
        try
        {
            await run(stoppingToken);
        }
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        {
            LogJobFailed(logger, exception, job);
        }
    }

    [LoggerMessage(EventId = 52, Level = LogLevel.Warning, Message = "The invitation {Job} job failed; trying again at the next interval.")]
    private static partial void LogJobFailed(ILogger logger, Exception exception, string job);
}
