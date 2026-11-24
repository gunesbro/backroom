using System.Diagnostics.CodeAnalysis;
using Backroom.ControlPlane.Contracts.V1;
using Backroom.Modules.Presence.Contracts;

namespace Backroom.Modules.ControlPlaneConnector.Infrastructure.Link;

/// <summary>
/// <c>AccountStatusChanged</c> → <see cref="IAccountStatusSink"/> (docs/contracts/phase-2.md, "Cell ↔ control plane link"), in the
/// receiver's unit of work. The cell applies only a newer version, so a resend or a late older message changes nothing. A message that
/// is not valid is refused for good (non-retryable <c>Nack</c>); a failure to store it (Valkey down) is an ordinary exception, so it is
/// sent again. The cell checks the shape again (lengths, ids), not the control plane's rules about what a person may set.
/// </summary>
internal sealed class AccountStatusChangedHandler(IAccountStatusSink sink) : IDownstreamHandler<AccountStatusChanged>
{
    public const string InvalidCode = "presence.account_status_invalid";

    /// <summary>The control plane's limits with room: one emoji of 16 code points, 100 characters of up to 4 code points, a zone id.</summary>
    internal const int MaxEmojiLength = 32;

    internal const int MaxTextLength = 800;

    internal const int MaxTimeZoneLength = 64;

    public async Task HandleAsync(Guid messageId, AccountStatusChanged message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!TryRead(message, out var status))
        {
            throw new LinkMessageRejectedException(InvalidCode);
        }

        await sink.ApplyAsync(status, cancellationToken);
    }

    /// <summary>Reads a message from the control plane (or a <c>FullSync</c> answer); <see langword="false"/> when it is not valid.</summary>
    internal static bool TryRead(AccountStatusChanged message, [NotNullWhen(true)] out AccountStatusCopy? status)
    {
        status = null;
        if (!Guid.TryParse(message.AccountId, out var accountId) || accountId == Guid.Empty
            || message.Version is 0 or > long.MaxValue
            || message.CustomEmoji.Length > MaxEmojiLength
            || message.CustomText.Length > MaxTextLength
            || message.TimeZone.Length > MaxTimeZoneLength)
        {
            return false;
        }

        try
        {
            status = new AccountStatusCopy(
                accountId,
                message.DndUntil?.ToDateTimeOffset(),
                message.CustomEmoji.Length == 0 ? null : message.CustomEmoji,
                message.CustomText.Length == 0 ? null : message.CustomText,
                message.CustomExpiresAt?.ToDateTimeOffset(),
                message.TimeZone.Length == 0 ? null : message.TimeZone,
                (long)message.Version);
            return true;
        }
        catch (InvalidOperationException)
        {
            // A timestamp outside the range a date can hold.
            return false;
        }
    }
}
