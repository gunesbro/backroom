using System.Diagnostics.CodeAnalysis;
using Backroom.ControlPlane.Contracts.V1;
using Backroom.Modules.Realtime.Contracts;

namespace Backroom.Modules.ControlPlaneConnector.Infrastructure.Link;

/// <summary>
/// <c>SessionRevoked</c> → <see cref="ISessionRevocation"/> (docs/contracts/phase-1.md, flow 5, step 3), in the receiver's unit of
/// work. A message that is not valid is refused for good (non-retryable <c>Nack</c>); a failure to store or announce the revocation
/// (Valkey down) is an ordinary exception, so the message is answered with a retryable <c>Nack</c> and sent again. Applying it
/// twice is harmless (the receiver also drops a repeated message id).
/// </summary>
internal sealed class SessionRevokedHandler(ISessionRevocation revocation) : IDownstreamHandler<SessionRevoked>
{
    public const string InvalidCode = "realtime.session_revocation_invalid";

    /// <summary>Keycloak session ids are UUIDs; the cell stores and looks up at most this many characters.</summary>
    internal const int MaxSessionIdLength = 128;

    public async Task HandleAsync(Guid messageId, SessionRevoked message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!TryRead(message, out var item))
        {
            throw new LinkMessageRejectedException(InvalidCode);
        }

        await revocation.RevokeAsync(item, cancellationToken);
    }

    /// <summary>Reads a message from the control plane (or a <c>FullSync</c> answer) into a revocation; <see langword="false"/> when it is not valid.</summary>
    internal static bool TryRead(SessionRevoked message, [NotNullWhen(true)] out SessionRevocation? revocation)
    {
        revocation = null;
        if (!Guid.TryParse(message.AccountId, out var accountId) || accountId == Guid.Empty
            || message.SessionId.Length > MaxSessionIdLength
            || message.RevokedAt is null || message.KeepUntil is null)
        {
            return false;
        }

        try
        {
            var revokedAt = message.RevokedAt.ToDateTimeOffset();
            var keepUntil = message.KeepUntil.ToDateTimeOffset();
            if (keepUntil < revokedAt)
            {
                return false;
            }

            revocation = new SessionRevocation(accountId, message.SessionId.Length == 0 ? null : message.SessionId, revokedAt, keepUntil);
            return true;
        }
        catch (InvalidOperationException)
        {
            // A timestamp outside the range a date can hold.
            return false;
        }
    }
}
