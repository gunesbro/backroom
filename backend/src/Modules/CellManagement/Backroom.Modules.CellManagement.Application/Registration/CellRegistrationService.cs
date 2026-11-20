using System.Text.Json;
using Microsoft.Extensions.Logging;
using Backroom.BuildingBlocks.Domain.Errors;

namespace Backroom.Modules.CellManagement.Application.Registration;

/// <summary>
/// Registers a cell with its public key (docs/contracts/phase-1.md, "Cell registration"): claims the
/// one-time key for that public key, creates or updates the cell's identity in Keycloak (outside any
/// transaction, idempotently, so a failed attempt can be retried with the same key and public key),
/// then marks the key used and the cell registered.
/// </summary>
internal sealed partial class CellRegistrationService(
    ICellRegistry registry,
    ICellIdentityProvider identityProvider,
    ILogger<CellRegistrationService> logger)
{
    public async Task<Result<CellRegistration>> RegisterAsync(string? registrationKey, JsonElement publicKey, string? version, CancellationToken cancellationToken)
    {
        if (!RegistrationKeys.HasValidShape(registrationKey))
        {
            LogRejected(logger, "malformed key");
            return CellManagementErrors.RegistrationKeyInvalid;
        }

        if (CellPublicKey.TryParse(publicKey) is not { } key)
        {
            LogRejected(logger, "invalid public key");
            return CellManagementErrors.PublicKeyInvalid;
        }

        var keyHash = RegistrationKeys.Hash(registrationKey!);
        var cellId = await registry.ClaimRegistrationKeyAsync(keyHash, key.Thumbprint, cancellationToken);
        if (cellId is null)
        {
            LogRejected(logger, "unknown, expired, used or differently claimed key");
            return CellManagementErrors.RegistrationKeyInvalid;
        }

        await identityProvider.RegisterCellAsync(cellId, key, cancellationToken);
        await registry.CompleteRegistrationAsync(keyHash, key.Thumbprint, Truncate(version), cancellationToken);
        LogRegistered(logger, cellId, key.Thumbprint);

        var (clientId, issuer) = identityProvider.CellCredentials(cellId);
        return new CellRegistration(cellId, clientId, issuer);
    }

    private static string Truncate(string? version) =>
        string.IsNullOrWhiteSpace(version) ? "unknown" : version.Length <= 64 ? version : version[..64];

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Security: cell registration rejected ({Reason}).")]
    private static partial void LogRejected(ILogger logger, string reason);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Security: cell {CellId} registered with key {KeyThumbprint}.")]
    private static partial void LogRegistered(ILogger logger, string cellId, string keyThumbprint);
}
