namespace Backroom.Modules.ControlPlaneConnector.Infrastructure.Registration;

/// <summary>
/// Keycloak refused the cell's client credentials (400 or 401: an unknown client or a key it does not hold), which
/// retrying does not fix, unlike a server error or an outage.
/// </summary>
internal sealed class CellCredentialsRejectedException : HttpRequestException
{
    public CellCredentialsRejectedException()
    {
    }

    public CellCredentialsRejectedException(string message)
        : base(message)
    {
    }

    public CellCredentialsRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public CellCredentialsRejectedException(int status)
        : base($"Keycloak refused the cell's credentials with status {status}.") => Status = status;

    public int Status { get; }
}
