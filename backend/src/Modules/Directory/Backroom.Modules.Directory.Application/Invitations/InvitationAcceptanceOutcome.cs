using Microsoft.Extensions.Logging;
using Backroom.ControlPlane.Contracts.V1;
using Backroom.Modules.CellManagement.Contracts;

namespace Backroom.Modules.Directory.Application.Invitations;

/// <summary>
/// The cell's answer to <c>InvitationAccepted</c> (docs/contracts/phase-1.md, flow 3, step 7, and "States"):
/// <c>Ack</c> makes the invitation <c>used</c> and the account's membership active; a non-retryable <c>Nack</c> (or
/// a delivery that expired) makes it <c>failed</c> with the code and removes a membership the acceptance made pending.
/// Only the cell the organization maps to can settle it, and its answer wins over a revocation requested meanwhile.
/// </summary>
internal sealed partial class InvitationAcceptanceOutcome(
    IInvitationRegistry invitations, TimeProvider timeProvider, ILogger<InvitationAcceptanceOutcome> logger)
    : ICellMessageOutcomeHandler<InvitationAccepted>
{
    public async Task OnAcknowledgedAsync(CellMessage<InvitationAccepted> message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (TryParse(message.Payload, out var organizationId, out var invitationId)
            && await invitations.CompleteAcceptanceAsync(organizationId, invitationId, message.CellId, timeProvider.GetUtcNow(), cancellationToken) is { } accountId)
        {
            LogUsed(logger, invitationId, organizationId, accountId, message.CellId);
        }
    }

    public async Task OnRejectedAsync(CellMessage<InvitationAccepted> message, string code, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrEmpty(code);
        if (TryParse(message.Payload, out var organizationId, out var invitationId)
            && await invitations.FailAcceptanceAsync(organizationId, invitationId, message.CellId, code, timeProvider.GetUtcNow(), cancellationToken) is { } accountId)
        {
            LogFailed(logger, invitationId, organizationId, accountId, message.CellId, code);
        }
    }

    private static bool TryParse(InvitationAccepted payload, out Guid organizationId, out Guid invitationId)
    {
        invitationId = Guid.Empty;
        return Guid.TryParse(payload.OrganizationId, out organizationId) && Guid.TryParse(payload.InvitationId, out invitationId);
    }

    [LoggerMessage(EventId = 27, Level = LogLevel.Information, Message = "Security: invitation {InvitationId} of organization {OrganizationId} was accepted by account {AccountId}; confirmed by cell {CellId}.")]
    private static partial void LogUsed(ILogger logger, Guid invitationId, Guid organizationId, Guid accountId, string cellId);

    [LoggerMessage(EventId = 28, Level = LogLevel.Warning, Message = "Security: accepting invitation {InvitationId} of organization {OrganizationId} by account {AccountId} failed at cell {CellId} ({Code}).")]
    private static partial void LogFailed(ILogger logger, Guid invitationId, Guid organizationId, Guid accountId, string cellId, string code);
}
