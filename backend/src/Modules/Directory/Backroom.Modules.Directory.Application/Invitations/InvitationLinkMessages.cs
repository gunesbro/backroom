using Microsoft.Extensions.Logging;
using Backroom.ControlPlane.Contracts.V1;
using Backroom.Modules.CellManagement.Contracts;

namespace Backroom.Modules.Directory.Application.Invitations;

/// <summary>
/// The cell's invitation messages (docs/contracts/phase-1.md, flow 3, steps 2 and 8, and "States"). The link has
/// checked that the organization belongs to the sending cell; the invitation id is looked up together with that
/// organization. A message that is malformed or names another organization's invitation is refused for good.
/// </summary>
internal sealed partial class InvitationLinkMessages(
    IInvitationRegistry registry,
    IInvitationEmailHmac emailHmac,
    TimeProvider timeProvider,
    ILogger<InvitationLinkMessages> logger)
    : ICellMessageHandler<InvitationRegistered>, ICellMessageHandler<InvitationRevoked>
{
    public const string InvalidCode = "directory.invitation_message_invalid";
    public const string OtherOrganizationCode = "directory.invitation_not_in_organization";
    public const string CodeTakenCode = "directory.invitation_code_conflict";

    /// <summary>A code hash is SHA-256, written as 64 lower-case hexadecimal characters.</summary>
    private const int CodeHashBytes = 32;

    // The range Timestamp can convert: 0001-01-01T00:00:00Z to 9999-12-31T23:59:59Z.
    private const long MinTimestampSeconds = -62_135_596_800;
    private const long MaxTimestampSeconds = 253_402_300_799;

    private static readonly string[] s_roles = ["admin", "member"];

    // Invitations live 7 days; an hour more allows for clocks. A registration whose expiry is more than 30 days past is
    // refused as nonsense, so InvitationJobs keeps revocation tombstones until such a late registration would be refused.
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(7) + TimeSpan.FromHours(1);
    public static readonly TimeSpan MaxExpiryAge = TimeSpan.FromDays(30);

    public async Task HandleAsync(CellMessage<InvitationRegistered> message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var payload = message.Payload;
        var now = timeProvider.GetUtcNow();
        if (!Guid.TryParse(payload.OrganizationId, out var organizationId)
            || !TryParseId(payload.InvitationId, out var invitationId)
            || !TryParseHash(payload.CodeHash, out var codeHash)
            || emailHmac.TryCompute(invitationId, payload.Email) is not { } hmac
            || !s_roles.Contains(payload.Role, StringComparer.Ordinal)
            || !IsValid(payload.ExpiresAt)
            || payload.CodeVersion is 0 or > int.MaxValue)
        {
            throw Refuse(InvalidCode, message);
        }

        var expiresAt = payload.ExpiresAt.ToDateTimeOffset();
        if (expiresAt > now + MaxLifetime || expiresAt < now - MaxExpiryAge)
        {
            throw Refuse(InvalidCode, message);
        }

        var registration = new InvitationRegistration(
            invitationId, organizationId, message.CellId, codeHash, hmac, payload.Role, expiresAt, (int)payload.CodeVersion);
        switch (await registry.RegisterAsync(registration, now, cancellationToken))
        {
            case RegistryOutcome.OtherOrganization:
                throw Refuse(OtherOrganizationCode, message);
            case RegistryOutcome.CodeTaken:
                throw Refuse(CodeTakenCode, message);
            case RegistryOutcome.Applied:
                LogRegistered(logger, invitationId, registration.CodeVersion, organizationId, message.CellId);
                break;
        }
    }

    public async Task HandleAsync(CellMessage<InvitationRevoked> message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!Guid.TryParse(message.Payload.OrganizationId, out var organizationId)
            || !TryParseId(message.Payload.InvitationId, out var invitationId))
        {
            throw Refuse(InvalidCode, message);
        }

        switch (await registry.RevokeAsync(organizationId, invitationId, message.CellId, timeProvider.GetUtcNow(), cancellationToken))
        {
            case RegistryOutcome.OtherOrganization:
                throw Refuse(OtherOrganizationCode, message);
            case RegistryOutcome.Applied:
                LogRevoked(logger, invitationId, organizationId, message.CellId);
                break;
            case RegistryOutcome.RevokeRequested:
                LogRevokeRequested(logger, invitationId, organizationId, message.CellId);
                break;
        }
    }

    // Protobuf throws when converting a timestamp outside 0001-01-01..9999-12-31 or with nanoseconds out of
    // range; that must be a refusal, not a failure the cell would retry.
    private static bool IsValid(Google.Protobuf.WellKnownTypes.Timestamp? timestamp) =>
        timestamp is { Seconds: >= MinTimestampSeconds and <= MaxTimestampSeconds, Nanos: >= 0 and <= 999_999_999 };

    private static bool TryParseId(string value, out Guid id) => Guid.TryParse(value, out id) && id != Guid.Empty;

    private static bool TryParseHash(string value, out byte[] hash)
    {
        hash = [];
        if (value is not { Length: CodeHashBytes * 2 } || !value.All(character => char.IsAsciiDigit(character) || character is >= 'a' and <= 'f'))
        {
            return false;
        }

        hash = Convert.FromHexString(value);
        return true;
    }

    private CellMessageRejectedException Refuse<T>(string code, CellMessage<T> message)
        where T : Google.Protobuf.IMessage
    {
        LogRefused(logger, typeof(T).Name, message.MessageId, message.CellId, code);
        return new CellMessageRejectedException(code);
    }

    [LoggerMessage(EventId = 21, Level = LogLevel.Information, Message = "Security: invitation {InvitationId} registered with code version {CodeVersion} for organization {OrganizationId} by cell {CellId}.")]
    private static partial void LogRegistered(ILogger logger, Guid invitationId, int codeVersion, Guid organizationId, string cellId);

    [LoggerMessage(EventId = 22, Level = LogLevel.Information, Message = "Security: invitation {InvitationId} revoked for organization {OrganizationId} by cell {CellId}.")]
    private static partial void LogRevoked(ILogger logger, Guid invitationId, Guid organizationId, string cellId);

    [LoggerMessage(EventId = 24, Level = LogLevel.Information, Message = "Security: revocation of invitation {InvitationId} requested while it is being accepted, for organization {OrganizationId} by cell {CellId}.")]
    private static partial void LogRevokeRequested(ILogger logger, Guid invitationId, Guid organizationId, string cellId);

    [LoggerMessage(EventId = 23, Level = LogLevel.Warning, Message = "Security: {MessageType} {MessageId} from cell {CellId} refused ({Code}).")]
    private static partial void LogRefused(ILogger logger, string messageType, Guid messageId, string cellId, string code);
}
