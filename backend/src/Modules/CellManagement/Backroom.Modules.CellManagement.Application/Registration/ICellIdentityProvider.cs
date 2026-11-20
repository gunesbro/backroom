namespace Backroom.Modules.CellManagement.Application.Registration;

/// <summary>The identity provider (Keycloak) side of a cell: its client, its user-token scope.</summary>
internal interface ICellIdentityProvider
{
    /// <summary>
    /// Creates or updates, idempotently, the cell's client (signed-JWT authentication with
    /// <paramref name="publicKey"/>) and the scope that gives user tokens the cell's audience.
    /// </summary>
    Task RegisterCellAsync(string cellId, CellPublicKey publicKey, CancellationToken cancellationToken);

    /// <summary>The client id and the token issuer the cell authenticates against.</summary>
    (string ClientId, string Issuer) CellCredentials(string cellId);
}
