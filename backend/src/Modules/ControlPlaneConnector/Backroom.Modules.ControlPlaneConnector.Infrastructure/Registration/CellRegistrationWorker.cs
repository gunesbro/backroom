using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Backroom.Modules.ControlPlaneConnector.Infrastructure.Health;

namespace Backroom.Modules.ControlPlaneConnector.Infrastructure.Registration;

/// <summary>
/// Worker role: registers the cell with the control plane on its first start
/// (docs/contracts/phase-1.md, "Cell registration", steps 2 and 3), retrying with back-off until it
/// succeeds. Once registered it does nothing; the link (later) authenticates with the cell's key.
/// </summary>
internal sealed partial class CellRegistrationWorker(
    CellKeyStore keyStore,
    CellCredentials credentials,
    CellLinkStatus status,
    IHttpClientFactory httpClientFactory,
    IOptions<ControlPlaneConnectorOptions> options,
    TimeProvider timeProvider,
    ILogger<CellRegistrationWorker> logger) : BackgroundService
{
    public const string HttpClientName = "control-plane";

    private const string MissingSettingsMessage =
        "The cell is not registered yet and has no registration key or control plane URL.";

    internal const string RegistrationRefusedReason =
        "The control plane refused the cell's registration: the registration key is used, expired or unknown, or the cell's key directory is not the one that registered.";

    private static readonly TimeSpan s_maxBackoff = TimeSpan.FromMinutes(1);

    // Client errors other than a timeout or rate limit do not go away by retrying.
    private static bool IsRejection(System.Net.HttpStatusCode status) =>
        (int)status is >= 400 and < 500 and not 408 and not 429;

    private static bool CanRegister(ControlPlaneConnectorOptions settings) =>
        !string.IsNullOrWhiteSpace(settings.RegistrationKey) && settings.ControlPlaneUrl is not null;

    // Relative to the control plane URL, so a URL with a base path (behind a proxy) keeps it.
    internal static Uri RegistrationUri(Uri controlPlaneUrl)
    {
        var baseUrl = controlPlaneUrl.AbsoluteUri.EndsWith('/') ? controlPlaneUrl : new Uri(controlPlaneUrl.AbsoluteUri + "/");
        return new Uri(baseUrl, "api/v1/cells/register");
    }

    /// <summary>Registers the cell unless it already is; returns whether it is registered now.</summary>
    internal async Task<bool> RegisterAsync(CancellationToken cancellationToken)
    {
        if (keyStore.IsRegistered)
        {
            return true;
        }

        var settings = options.Value;
        if (!CanRegister(settings))
        {
            throw new InvalidOperationException(MissingSettingsMessage);
        }

        // The key is saved before the request, so a retry sends the same public key.
        var request = new JsonObject
        {
            ["registrationKey"] = settings.RegistrationKey,
            ["publicKey"] = await credentials.GetPublicJwkAsync(cancellationToken),
            ["version"] = typeof(CellRegistrationWorker).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
        };

        using var http = httpClientFactory.CreateClient(HttpClientName);
        using var response = await http.PostAsJsonAsync(RegistrationUri(settings.ControlPlaneUrl!), request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            LogRegistrationRefused(logger, (int)response.StatusCode);
            if (IsRejection(response.StatusCode))
            {
                // A used, expired or unknown key: only an operator can fix it (a new registration key, or the cell's key
                // directory restored); report it while retrying.
                status.Reject(RegistrationRefusedReason);
            }

            return false;
        }

        var registration = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken);
        if (!string.Equals(registration?["cellId"]?.GetValue<string>(), settings.CellId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The control plane registered the key for a different cell id.");
        }

        await keyStore.MarkRegisteredAsync(settings.CellId, cancellationToken);
        status.Clear();
        LogRegistered(logger, settings.CellId);
        return true;
    }

    [SuppressMessage("Design", "CA1031", Justification = "Registration retries through control plane and Keycloak outages; failures are logged.")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A missing setting does not fix itself: the host stops instead of retrying forever.
        if (!keyStore.IsRegistered && !CanRegister(options.Value))
        {
            LogMissingSettings(logger);
            throw new InvalidOperationException(MissingSettingsMessage);
        }

        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await RegisterAsync(stoppingToken))
                {
                    return;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                LogRegistrationFailed(logger, exception);
            }

            failures++;
            var delay = TimeSpan.FromMilliseconds(Math.Min(s_maxBackoff.TotalMilliseconds, 1000 * Math.Pow(2, Math.Min(failures - 1, 10))));
            try
            {
                await Task.Delay(delay, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Security: cell {CellId} registered with the control plane.")]
    private static partial void LogRegistered(ILogger logger, string cellId);

    [LoggerMessage(Level = LogLevel.Critical, Message = "The cell is not registered and ControlPlaneConnector:RegistrationKey or ControlPlaneConnector:ControlPlaneUrl is missing; stopping.")]
    private static partial void LogMissingSettings(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "The control plane refused the cell's registration with status {Status}; retrying.")]
    private static partial void LogRegistrationRefused(ILogger logger, int status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cell registration failed; retrying.")]
    private static partial void LogRegistrationFailed(ILogger logger, Exception exception);
}
