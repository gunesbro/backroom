using Backroom.BuildingBlocks.Domain.Errors;
using Backroom.ControlPlane.Contracts.V1;
using Backroom.Modules.Organization.Contracts;

namespace Backroom.Modules.ControlPlaneConnector.Infrastructure.Link;

/// <summary>
/// <c>InvitationAccepted</c> → <see cref="IInvitationAcceptance"/> (docs/contracts/phase-1.md, flow 3, step 6), in
/// the receiver's unit of work, which is already in the invitation's organization. A business rejection is a
/// non-retryable <c>Nack</c> with Organization's code (the control plane fails the acceptance); an organization or
/// invitation the cell does not know yet is a retryable one, never an <c>Ack</c>.
/// </summary>
internal sealed class InvitationAcceptedHandler(IInvitationAcceptance acceptance) : IDownstreamHandler<InvitationAccepted>
{
    public const string InvalidCode = "organization.invitation_acceptance_invalid";

    public async Task HandleAsync(Guid messageId, InvitationAccepted message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!Guid.TryParse(message.InvitationId, out var invitationId)
            || !Guid.TryParse(message.AccountId, out var accountId)
            || message.AcceptedAt is null)
        {
            throw new LinkMessageRejectedException(InvalidCode);
        }

        DateTimeOffset acceptedAt;
        try
        {
            acceptedAt = message.AcceptedAt.ToDateTimeOffset();
        }
        catch (InvalidOperationException exception)
        {
            // Outside the range a timestamp may hold.
            throw new LinkMessageRejectedException(InvalidCode, exception);
        }

        var result = await acceptance.AcceptAsync(
            new AcceptedInvitation(invitationId, accountId, message.DisplayName, message.Email, message.Locale, acceptedAt),
            cancellationToken);
        if (result.Error is not { } error)
        {
            return;
        }

        if (error.Kind == ErrorKind.NotFound)
        {
            throw new LinkPrerequisiteMissingException(error.Code);
        }

        throw new LinkMessageRejectedException(error.Code);
    }
}
